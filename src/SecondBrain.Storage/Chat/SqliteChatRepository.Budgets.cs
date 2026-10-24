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
        var day = AuthTime.Format(new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero));
        var hour = AuthTime.Format(now.AddHours(-1));
        var policy = Policy(turn);
        var daily = await connection.ExecuteScalarAsync<long>(Sql("""
            SELECT (SELECT COALESCE(SUM(charged_tokens),0) FROM chat_model_calls WHERE created_at>=@day)
              +(SELECT COALESCE(SUM(charged_tokens),0) FROM chat_usage_history WHERE created_at>=@day)
            """, new { day }, transaction, token));
        var hourly = await connection.ExecuteScalarAsync<long>(Sql("""
            SELECT (SELECT COALESCE(SUM(charged_tokens),0) FROM chat_model_calls WHERE credential_id=@actor AND created_at>=@hour)
              +(SELECT COALESCE(SUM(charged_tokens),0) FROM chat_usage_history WHERE credential_id=@actor AND created_at>=@hour)
            """, new { actor = actor.Id, hour }, transaction, token));
        if (tokens > policy.DailyTokens - daily || tokens > policy.HourlyTokens - hourly) throw new ChatLimitException("The model token budget is exhausted.");
        await connection.ExecuteAsync(Sql("""
            INSERT INTO chat_model_calls(turn_id,call_id,credential_id,reserved_tokens,charged_tokens,status,created_at)
            VALUES(@turnId,@callId,@actor,@tokens,@tokens,'reserved',@now)
            """, new { turnId, callId, actor = actor.Id, tokens, now = AuthTime.Format(now) }, transaction, token));
        return 0;
    }, cancellationToken).AsTask());

    public ValueTask SettleTokensAsync(string turnId, string callId, long? actualTokens, CancellationToken cancellationToken = default) => new(state.QueueWriteAsync(async (connection, transaction, token) =>
    {
        if (actualTokens is < 0 or > 10_000_000) throw new ArgumentOutOfRangeException(nameof(actualTokens));
        // Unknown usage (including cancellation/crash) consumes the reservation, rather than granting a refund.
        return await connection.ExecuteAsync(Sql("""
            UPDATE chat_model_calls SET charged_tokens=COALESCE(@actualTokens,reserved_tokens),status='settled'
            WHERE turn_id=@turnId AND call_id=@callId AND status='reserved'
            """, new { turnId, callId, actualTokens }, transaction, token));
    }, cancellationToken).AsTask());
}
