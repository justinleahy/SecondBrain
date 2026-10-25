using SecondBrain.Core.Providers;
using SecondBrain.Storage.Usage;
using Dapper;
using System.Text.Json;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Chat;

namespace SecondBrain.Storage.Chat;

public sealed partial class SqliteChatRepository
{
    public ValueTask RecordProviderExposureAsync(string turnId, string callId, AuthenticatedCredential actor, CancellationToken cancellationToken = default, ChatProviderTarget? target = null) =>
        new(state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            var turn = await RunningAsync(connection, transaction, turnId, actor, token);
            var policy = Policy(turn);
            var refs = await ReferencesAsync(connection, transaction, turnId, token);
            await RequireLiveLineageAsync(connection, transaction, refs, token);
            if (await connection.ExecuteScalarAsync<int>(Sql("SELECT COUNT(*) FROM chat_model_calls WHERE turn_id=@turnId AND call_id=@callId AND status='reserved'", new { turnId, callId }, transaction, token)) != 1)
                throw new ChatConflictException("The provider exposure needs an active model reservation.");
            if (target is not null)
                await connection.ExecuteAsync(Sql("UPDATE usage SET provider=@Provider,model=@Model,role='embed' WHERE id=(SELECT usage_id FROM usage_calls WHERE call_id=@call)",
                    new { target.Provider, target.Model, call = "chat/" + turnId + "/" + callId }, transaction, token));
            return await connection.ExecuteAsync(Sql("""
                INSERT INTO chat_provider_exposures(turn_id,call_id,provider,model,endpoint,documents_json)
                VALUES(@turnId,@callId,@provider,@model,@endpoint,@documents)
                """, new { turnId, callId, provider = target?.Provider ?? policy.Provider, model = target?.Model ?? policy.Model,
                    endpoint = target is null ? policy.ProviderEndpoint : target.Endpoint, documents = JsonSerializer.Serialize(refs, Json) }, transaction, token));
        }, cancellationToken).AsTask());

    public ValueTask ReserveTokensAsync(string turnId, string callId, long tokens, AuthenticatedCredential actor,
        CancellationToken cancellationToken = default) => new(state.QueueWriteAsync(async (connection, transaction, token) =>
    {
        if (tokens is < 1 or > 10_000_000 || string.IsNullOrWhiteSpace(callId) || callId.Length > 128) throw new ArgumentException("Invalid model reservation.");
        var turn = await RunningAsync(connection, transaction, turnId, actor, token);
        await RequireLiveLineageAsync(connection, transaction, await ReferencesAsync(connection, transaction, turnId, token), token);
        var previous = await connection.ExecuteScalarAsync<long?>(Sql("SELECT reserved_tokens FROM chat_model_calls WHERE turn_id=@turnId AND call_id=@callId", new { turnId, callId }, transaction, token));
        if (previous is not null)
        {
            // A reservation is single-use. Reusing it could send a second uncharged provider request.
            throw new ChatConflictException("This model call ID has already been reserved.");
        }
        var now = clock.GetUtcNow();
        var policy = Policy(turn);
        try
        {
            await SqliteModelUsage.ReserveInTransactionAsync(connection, transaction, "chat/" + turnId + "/" + callId,
                policy.Provider, policy.Model, "chat", tokens, actor.Id, actor.Generation, actor.AccountEpoch,
                null, turnId, now, Math.Min(policy.DailyTokens, options?.CurrentValue.Budgets.DailyTokens ?? policy.DailyTokens),
                Math.Min(policy.HourlyTokens, options?.CurrentValue.Limits.PerCredential.ModelTokensPerHour ?? policy.HourlyTokens), token);
        }
        catch (ModelBudgetException) { throw new ChatLimitException("The model token budget is exhausted."); }
        await connection.ExecuteAsync(Sql("""
            INSERT INTO chat_model_calls(turn_id,call_id,credential_id,reserved_tokens,charged_tokens,status,created_at)
            VALUES(@turnId,@callId,@actor,@tokens,@tokens,'reserved',@now)
            """, new { turnId, callId, actor = actor.Id, tokens, now = AuthTime.Format(now) }, transaction, token));
        return 0;
    }, cancellationToken).AsTask());

    public ValueTask SettleTokensAsync(string turnId, string callId, long? actualTokens, CancellationToken cancellationToken = default)
    {
        if (actualTokens is < 0 or > 10_000_000) throw new ArgumentOutOfRangeException(nameof(actualTokens));
        return SettleUsageAsync(turnId, callId, actualTokens is { } actual ? new(null, null, null, actual) : null, cancellationToken);
    }

    public ValueTask SettleUsageAsync(string turnId, string callId, ModelUsageTotals? totals, CancellationToken cancellationToken = default) => new(state.QueueWriteAsync(async (connection, transaction, token) =>
    {
        var usageId = await connection.ExecuteScalarAsync<long?>(Sql("SELECT usage_id FROM usage_calls WHERE call_id=@call",
            new { call = "chat/" + turnId + "/" + callId }, transaction, token));
        if (usageId is { } id) await SqliteModelUsage.SettleInTransactionAsync(connection, transaction, id, totals, token);
        long? legacy = totals?.TotalTokens is >= 0 and <= 100_000_000 ? totals.TotalTokens : null;
        // Unknown usage (including cancellation/crash) consumes the reservation, rather than granting a refund.
        return await connection.ExecuteAsync(Sql("""
            UPDATE chat_model_calls SET charged_tokens=COALESCE((SELECT charged_tokens FROM usage_calls WHERE usage_id=@usageId),@legacy,reserved_tokens),status='settled'
            WHERE turn_id=@turnId AND call_id=@callId AND status='reserved'
            """, new { turnId, callId, usageId, legacy }, transaction, token));
    }, cancellationToken).AsTask());
}
