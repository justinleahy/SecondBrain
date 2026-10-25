using SecondBrain.Core.Auth;
using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Providers;
using SecondBrain.Storage.Auth;

namespace SecondBrain.Storage.Usage;

public sealed class SqliteModelUsage(IStateStore state, IOptions<SecondBrainOptions> options, TimeProvider clock,
    IOptionsMonitor<SecondBrainOptions>? monitor = null) : IModelUsage
{
    private SecondBrainOptions Current => monitor?.CurrentValue ?? options.Value;

    public Task<long> ReserveAsync(ModelUsageRequest request, CancellationToken token = default) =>
        state.QueueWriteAsync(async (c, t, ct) =>
        {
            var now = clock.GetUtcNow();
            string? credential = null, job = null;
            long? generation = null, epoch = null;
            if (request.Actor is { } actor)
            {
                var scope = request.Role switch { "enrich" => Scope.Infer, "classify" => Scope.Write, _ => Scope.Read };
                await IngestionAuthorityValidator.ValidateAsync(c, t, actor, scope, now, cancellationToken: ct);
                credential = actor.CredentialId; generation = actor.CredentialGeneration; epoch = actor.AccountEpoch;
            }
            if (request.DocumentId is { } document)
            {
                if (request.Revision is not { } revision || await c.ExecuteScalarAsync<int>(Sql(
                    "SELECT COUNT(*) FROM documents WHERE id=@document AND revision=@revision AND status NOT IN ('trashed','purged','removed_from_source','suppressed')",
                    new { document, revision }, t, ct)) != 1) throw new InvalidOperationException("The inference document changed.");
                var source = await c.QuerySingleOrDefaultAsync<JobActor>(Sql("""
                    SELECT id Job,initiating_credential_id Credential,credential_generation Generation,account_epoch Epoch
                    FROM jobs WHERE document_id=@document AND revision=@revision ORDER BY created_at DESC,id DESC LIMIT 1
                    """, new { document, revision }, t, ct));
                if (source is not null)
                {
                    job = source.Job;
                }
                if (source?.Credential is null)
                    source = await c.QuerySingleOrDefaultAsync<JobActor>(Sql("""
                        SELECT j.id Job,j.initiating_credential_id Credential,j.credential_generation Generation,j.account_epoch Epoch
                        FROM jobs j WHERE j.initiating_credential_id IS NOT NULL AND
                          ((j.document_id=@document AND j.revision=@revision) OR
                           (j.document_id=(SELECT container_document_id FROM documents WHERE id=@document) AND
                            j.revision=(SELECT p.revision FROM documents d JOIN documents p ON p.id=d.container_document_id WHERE d.id=@document)))
                        ORDER BY (j.document_id=@document) DESC,j.created_at DESC,j.id DESC LIMIT 1
                        """, new { document, revision }, t, ct));
                // Derived embedding jobs retain the explicit import's attribution, including calendar members.
                // Admitted ingestion continues as the daemon but remains charged to its initiator.
                if (credential is null && source is not null)
                { credential = source.Credential; generation = source.Generation; epoch = source.Epoch; job ??= source.Job; }
            }
            if (request.JobId is { } explicitJob)
            {
                if (request.DocumentId is null || request.Revision is null || await c.ExecuteScalarAsync<int>(Sql(
                    "SELECT COUNT(*) FROM jobs WHERE id=@explicitJob AND document_id=@document AND revision=@revision AND status='running' AND kind=@role",
                    new { explicitJob, document = request.DocumentId, revision = request.Revision, role = request.Role }, t, ct)) != 1)
                    throw new InvalidOperationException("The inference job changed.");
                job = explicitJob;
            }
            return await ReserveInTransactionAsync(c, t, request.CallId, request.Provider, request.Model, request.Role,
                request.ReservedTokens, credential, generation, epoch, job, null, now,
                Current.Budgets.DailyTokens, Current.Limits.PerCredential.ModelTokensPerHour, ct);
        }, token).AsTask();

    public Task SettleAsync(long id, ModelUsageTotals? totals, CancellationToken token = default) =>
        state.QueueWriteAsync(async (c, t, ct) => { await SettleInTransactionAsync(c, t, id, totals, ct); return 0; }, token).AsTask();

    public Task<ModelUsageReport> ReadAsync(IngestionAuthority actor, DateTimeOffset from, DateTimeOffset to, CancellationToken token = default) =>
        state.QueueWriteAsync(async (c, t, ct) =>
        {
            if (to <= from || to - from > TimeSpan.FromDays(31)) throw new ArgumentException("Usage intervals must be at most 31 days.");
            await IngestionAuthorityValidator.ValidateAsync(c, t, actor, Scope.Admin, clock.GetUtcNow(), cancellationToken: ct);
            var rows = (await c.QueryAsync<UsageGroup>(Sql("""
                SELECT u.provider Provider,u.model Model,u.role Role,u.credential_id CredentialId,u.job_id JobId,
                  COUNT(*) Calls,SUM(u.reserved_tokens) ReservedTokens,SUM(COALESCE(c.charged_tokens,u.reserved_tokens)) ChargedTokens,
                  CASE WHEN COUNT(u.input_tokens)=COUNT(*) THEN SUM(u.input_tokens) END InputTokens,
                  CASE WHEN COUNT(u.output_tokens)=COUNT(*) THEN SUM(u.output_tokens) END OutputTokens,
                  CASE WHEN COUNT(u.cached_input_tokens)=COUNT(*) THEN SUM(u.cached_input_tokens) END CachedInputTokens,
                  SUM(CASE WHEN u.settled=0 THEN 1 ELSE 0 END) UnsettledCalls
                FROM usage u LEFT JOIN usage_calls c ON c.usage_id=u.id WHERE u.at>=@start AND u.at<@end
                GROUP BY u.provider,u.model,u.role,u.credential_id,u.job_id ORDER BY MIN(u.at),MIN(u.id) LIMIT 501
                """, new { start = AuthTime.Format(from), end = AuthTime.Format(to) }, t, ct))).Select(r => new ModelUsageRow(r.Provider,r.Model,r.Role,r.CredentialId,r.JobId,r.Calls,r.ReservedTokens,r.ChargedTokens,r.InputTokens,r.OutputTokens,r.CachedInputTokens,r.UnsettledCalls)).ToList();
            // Old chat reservations have no model-level ledger. Keep their spend visible and charged.
            var legacy = await LegacyTotalAsync(c, t, AuthTime.Format(from), AuthTime.Format(to), null, ct);
            if (legacy != 0 && rows.Count < 501) rows.Add(new(null, null, "legacy-chat", null, null, 0, legacy, legacy, null, null, null, 0));
            var day = AuthTime.Format(new DateTimeOffset(clock.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero));
            var daily = await TotalAsync(c, t, day, null, ct);
            return new ModelUsageReport(from, to, daily, Current.Budgets.DailyTokens, rows.Take(500).ToArray(), rows.Count > 500);
        }, token).AsTask();

    internal static async Task<long> ReserveInTransactionAsync(IDbConnection c, IDbTransaction t, string call,
        string provider, string model, string role, long tokens, string? credential, long? generation, long? epoch,
        string? job, string? turn, DateTimeOffset now, long dailyLimit, long hourlyLimit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(call) || call.Length > 300 || string.IsNullOrWhiteSpace(provider) || provider.Length > 200 ||
            string.IsNullOrWhiteSpace(model) || model.Length > 500 || role is not ("chat" or "embed" or "rerank" or "enrich" or "classify") || tokens is < 1 or > 100_000_000)
            throw new ArgumentException("Invalid model usage reservation.");
        if (await c.ExecuteScalarAsync<int>(Sql("SELECT COUNT(*) FROM usage_calls WHERE call_id=@call", new { call }, t, ct)) != 0)
            throw new InvalidOperationException("This provider call is already reserved.");
        var daily = await TotalAsync(c, t, AuthTime.Format(new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero)), null, ct);
        var hourly = credential is null ? 0 : await TotalAsync(c, t, AuthTime.Format(now.AddHours(-1)), credential, ct);
        if (tokens > dailyLimit - daily || credential is not null && tokens > hourlyLimit - hourly) throw new ModelBudgetException();
        var id = await c.ExecuteScalarAsync<long>(Sql("""
            INSERT INTO usage(at,provider,model,role,turn_id,credential_id,job_id,reserved_tokens)
            VALUES(@at,@provider,@model,@role,@turn,@credential,@job,@tokens);
            SELECT last_insert_rowid();
            """, new { at = AuthTime.Format(now), provider, model, role, turn, credential, job, tokens }, t, ct));
        await c.ExecuteAsync(Sql("INSERT INTO usage_calls(call_id,usage_id,charged_tokens,credential_generation,account_epoch) VALUES(@call,@id,@tokens,@generation,@epoch)",
            new { call, id, tokens, generation, epoch }, t, ct));
        return id;
    }

    internal static async Task SettleInTransactionAsync(IDbConnection c, IDbTransaction t, long id, ModelUsageTotals? totals, CancellationToken ct)
    {
        var values = new[] { totals?.InputTokens, totals?.OutputTokens, totals?.CachedInputTokens, totals?.TotalTokens };
        if (values.Any(v => v is < 0 or > 100_000_000) || totals?.CachedInputTokens > totals?.InputTokens
            || totals?.TotalTokens is { } reported && reported < (totals.InputTokens ?? 0) + (totals.OutputTokens ?? 0)) totals = null;
        long? actual = totals?.TotalTokens ?? (totals?.InputTokens is { } input && totals.OutputTokens is { } output ? input + output : null);
        await c.ExecuteAsync(Sql("""
            UPDATE usage_calls SET charged_tokens=COALESCE(@actual,charged_tokens)
              WHERE usage_id=@id AND EXISTS(SELECT 1 FROM usage WHERE id=@id AND settled=0);
            UPDATE usage SET input_tokens=@input,output_tokens=@output,cached_input_tokens=@cached,settled=1
              WHERE id=@id AND settled=0;
            """, new { id, actual, input = totals?.InputTokens, output = totals?.OutputTokens, cached = totals?.CachedInputTokens }, t, ct));
    }

    internal static async Task<long> TotalAsync(IDbConnection c, IDbTransaction t, string start, string? credential, CancellationToken ct)
    {
        var recorded = await c.ExecuteScalarAsync<long>(Sql("""
            SELECT COALESCE(SUM(COALESCE(c.charged_tokens,u.reserved_tokens)),0)
            FROM usage u LEFT JOIN usage_calls c ON c.usage_id=u.id WHERE u.at>=@start AND (@credential IS NULL OR u.credential_id=@credential)
            """, new { start, credential }, t, ct));
        return checked(recorded + await LegacyTotalAsync(c, t, start, null, credential, ct));
    }
    private static Task<long> LegacyTotalAsync(IDbConnection c, IDbTransaction t, string start, string? end, string? credential, CancellationToken ct) =>
        c.ExecuteScalarAsync<long>(Sql("""
            SELECT (SELECT COALESCE(SUM(x.charged_tokens),0) FROM chat_model_calls x
              WHERE x.created_at>=@start AND (@end IS NULL OR x.created_at<@end) AND (@credential IS NULL OR x.credential_id=@credential)
                AND NOT EXISTS(SELECT 1 FROM usage_calls c WHERE c.call_id='chat/'||x.turn_id||'/'||x.call_id))
              +(SELECT COALESCE(SUM(charged_tokens),0) FROM chat_usage_history
                WHERE created_at>=@start AND (@end IS NULL OR created_at<@end) AND (@credential IS NULL OR credential_id=@credential))
            """, new { start, end, credential }, t, ct));
    private static CommandDefinition Sql(string text, object? args, IDbTransaction t, CancellationToken ct) => new(text, args, t, cancellationToken: ct);
    private sealed class UsageGroup
    {
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public string? Role { get; set; }
        public string? CredentialId { get; set; }
        public string? JobId { get; set; }
        public long Calls { get; set; }
        public long ReservedTokens { get; set; }
        public long ChargedTokens { get; set; }
        public long? InputTokens { get; set; }
        public long? OutputTokens { get; set; }
        public long? CachedInputTokens { get; set; }
        public long UnsettledCalls { get; set; }
    }
    private sealed record JobActor(string Job, string? Credential, long? Generation, long? Epoch);
}
