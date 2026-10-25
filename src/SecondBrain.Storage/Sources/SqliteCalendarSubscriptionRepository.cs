using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;

namespace SecondBrain.Storage.Sources;

public sealed partial class SqliteCalendarSubscriptionRepository(IStateStore state, TimeProvider clock, IOptionsMonitor<SecondBrainOptions>? options = null, SecondBrain.Core.Durability.IPublicationCoordinator? publications = null, IManagedDocumentFiles? files = null)
    : ICalendarSubscriptionRepository
{
    public async ValueTask<CalendarSubscription> CreateAsync(string name, string origin, string protectedUrl, int interval, bool enabled, IngestionAuthority actor, CancellationToken token = default)
    {
        if (name is null) throw new CalendarSubscriptionException("calendar_settings_invalid");
        CalendarSubscriptionService.Validate(name, interval);
        _ = CalendarLink.ValidateOrigin(origin);
        if (string.IsNullOrEmpty(protectedUrl) || protectedUrl.Length > 32768 || Uri.TryCreate(protectedUrl, UriKind.Absolute, out _))
            throw new CalendarSubscriptionException("calendar_secret_invalid");
        var id = Ulid.NewUlid().ToString(); var now = Format(clock.GetUtcNow());
        using var gate = await PublicationCoordinator.AcquirePublicationGateAsync(state, token);
        await state.QueueWriteAsync(async (connection, transaction, ct) =>
        {
            await IngestionAuthorityValidator.ValidateAsync(connection, transaction, actor, Scope.Admin, clock.GetUtcNow(), cancellationToken: ct);
            if (enabled) await CheckQueueCapacity(connection, transaction, ct);
            await connection.ExecuteAsync(Sql("""
                INSERT INTO sources(id,kind,name,config_json,status,created_at) VALUES(@id,'calendar_subscription',@name,@config,@status,@now);
                INSERT INTO calendar_subscriptions(source_id,container_id,origin,protected_url,poll_interval_s,requested,next_attempt_at)
                VALUES(@id,@container,@origin,@secret,@interval,@requested,@due);
                """, new { id, name, config = JsonSerializer.Serialize(new { origin, poll_interval_s = interval }), status = enabled ? "active" : "disabled", now,
                container = Ulid.NewUlid().ToString(), origin, secret = protectedUrl, interval, requested = enabled ? 1 : 0, due = enabled ? now : null }, transaction, ct));
            return 0;
        }, token);
        return (await GetAsync(id, token))!;
    }

    public async ValueTask<IReadOnlyList<CalendarSubscription>> ListAsync(CancellationToken token = default)
    {
        await using var lease = await state.OpenReadConnectionAsync(token);
        var rows = await lease.Connection.QueryAsync<Row>(Sql(Select + " WHERE s.status<>'removed' ORDER BY s.created_at,s.id", null, null, token));
        return rows.Select(View).ToArray();
    }
    public async ValueTask<CalendarSubscription?> GetAsync(string id, CancellationToken token = default, bool includeRemoved = false)
    {
        await using var lease = await state.OpenReadConnectionAsync(token);
        var row = await lease.Connection.QuerySingleOrDefaultAsync<Row>(Sql(Select + " WHERE s.id=@id AND (@includeRemoved=1 OR s.status<>'removed')", new { id, includeRemoved }, null, token));
        return row is null ? null : View(row);
    }
    public async ValueTask UpdateAsync(string id, string? name, int? interval, bool? enabled, IngestionAuthority actor, CancellationToken token = default)
    {
        CalendarSubscriptionService.Validate(name, interval);
        await UnderControlGateAsync(id, async () =>
        {
        await state.QueueWriteAsync(async (connection, transaction, ct) =>
        {
            await IngestionAuthorityValidator.ValidateAsync(connection, transaction, actor, Scope.Admin, clock.GetUtcNow(), cancellationToken: ct);
            var row = await connection.QuerySingleOrDefaultAsync<Row>(Sql(Select + " WHERE s.id=@id AND s.status<>'removed'", new { id }, transaction, ct)) ?? throw new IngestionConflictException("not-found");
            var active = enabled ?? row.Status == "active"; var poll = interval ?? row.Interval; var title = name?.Trim() ?? row.Name;
            if (active && await connection.ExecuteScalarAsync<long>(Sql("SELECT COUNT(*) FROM documents WHERE id=@ContainerId AND status IN ('deleted','purged','missing','removed_from_source')", row, transaction, ct)) != 0)
                throw new CalendarSubscriptionException("calendar_container_unavailable");
            if (active && await connection.ExecuteScalarAsync<long>(Sql("SELECT requested+CASE WHEN lease_id IS NULL THEN 0 ELSE 1 END FROM calendar_subscriptions WHERE source_id=@id", new { id }, transaction, ct)) == 0)
                await CheckQueueCapacity(connection, transaction, ct);
            var now = Format(clock.GetUtcNow());
            await connection.ExecuteAsync(Sql("""
                UPDATE sources SET name=@name,status=@status,config_json=json_set(config_json,'$.poll_interval_s',@interval) WHERE id=@id;
                UPDATE calendar_subscriptions SET poll_interval_s=@interval,generation=generation+1,lease_id=NULL,lease_until=NULL,
                  requested=@requested,deep_requested=0,next_attempt_at=@next WHERE source_id=@id;
                DELETE FROM calendar_lifecycle_pauses WHERE source_id=@id AND @explicitResume=1;
                """, new { id, explicitResume = enabled == true ? 1 : 0, name = title, status = active ? "active" : "disabled", config = JsonSerializer.Serialize(new { origin = row.Origin, poll_interval_s = poll }), interval = poll,
                requested = active ? 1 : 0, next = active ? now : null }, transaction, ct));
            return 0;
        }, token);
        }, token);
    }
    public async ValueTask QueueAsync(string id, bool deep, IngestionAuthority actor, CancellationToken token = default)
    {
        using var gate = await PublicationCoordinator.AcquirePublicationGateAsync(state, token);
        await state.QueueWriteAsync(async (connection, transaction, ct) =>
        {
            await IngestionAuthorityValidator.ValidateAsync(connection, transaction, actor, Scope.Admin, clock.GetUtcNow(), cancellationToken: ct);
            var status = await connection.ExecuteScalarAsync<string?>(Sql("SELECT status FROM sources WHERE id=@id AND kind='calendar_subscription' AND status<>'removed'", new { id }, transaction, ct));
            if (status is null) throw new IngestionConflictException("not-found");
            if (status != "active") throw new CalendarSubscriptionException("calendar_source_paused");
            var requested = await connection.ExecuteScalarAsync<long>(Sql("SELECT requested FROM calendar_subscriptions WHERE source_id=@id", new { id }, transaction, ct));
            if (requested == 0) await CheckQueueCapacity(connection, transaction, ct);
            await connection.ExecuteAsync(Sql("UPDATE calendar_subscriptions SET requested=1,deep_requested=MAX(deep_requested,@deep),next_attempt_at=@now WHERE source_id=@id", new { id, deep = deep ? 1 : 0, now = Format(clock.GetUtcNow()) }, transaction, ct));
            return 0;
        }, token);
    }

    public async ValueTask<IReadOnlyList<CalendarRefreshWork>> ClaimDueAsync(int limit = 4, CancellationToken token = default)
    {
        if (limit is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(limit));
        using var gate = await PublicationCoordinator.AcquirePublicationGateAsync(state, token);
        return await state.QueueWriteAsync<IReadOnlyList<CalendarRefreshWork>>(async (connection, transaction, ct) =>
        {
            var now = Format(clock.GetUtcNow());
            var rows = (await connection.QueryAsync<WorkRow>(Sql("""
                SELECT c.source_id SourceId,c.container_id ContainerId,c.generation Generation,c.protected_url ProtectedUrl,c.etag ETag,c.last_modified Modified,
                  c.snapshot_hash SnapshotHash,c.snapshot_path SnapshotPath,c.rejected_hash RejectedHash,c.rejected_generation RejectedGeneration,c.deep_requested Deep
                FROM calendar_subscriptions c JOIN sources s ON s.id=c.source_id
                WHERE s.status='active' AND (c.requested=1 OR c.next_attempt_at<=@now) AND (c.lease_id IS NULL OR c.lease_until<=@now)
                  AND NOT EXISTS(SELECT 1 FROM documents d WHERE d.id=c.container_id AND d.status IN ('deleted','purged','missing','removed_from_source'))
                  AND NOT EXISTS(SELECT 1 FROM meta WHERE key='mutation_document_reservation:'||c.container_id)
                  AND NOT EXISTS(SELECT 1 FROM calendar_batches b WHERE b.container_id=c.container_id AND b.phase='publishing')
                ORDER BY c.next_attempt_at,c.source_id LIMIT @limit
                """, new { now, limit }, transaction, ct))).ToArray();
            var result = new List<CalendarRefreshWork>();
            foreach (var row in rows)
            {
                var lease = Ulid.NewUlid().ToString();
                await connection.ExecuteAsync(Sql("""
                    UPDATE calendar_subscriptions SET generation=generation+1,lease_id=@lease,lease_until=@until,last_attempt_at=@now,requested=0,deep_requested=0 WHERE source_id=@id;
                    """, new { id = row.SourceId, lease, until = Format(clock.GetUtcNow().AddMinutes(4)), now }, transaction, ct));
                result.Add(new(row.SourceId, row.ContainerId, row.Generation + 1, lease, row.ProtectedUrl, row.Deep != 0 ? null : row.ETag, row.Deep != 0 ? null : Parse(row.Modified), row.SnapshotHash, row.SnapshotPath, row.RejectedHash, row.RejectedGeneration, row.Deep != 0));
            }
            return result;
        }, token);
    }
    public async ValueTask<bool> IsCurrentAsync(string sourceId, long generation, CancellationToken token = default)
    {
        await using var lease = await state.OpenReadConnectionAsync(token);
        return await lease.Connection.ExecuteScalarAsync<long>(Sql("""
            SELECT COUNT(*) FROM calendar_subscriptions c JOIN sources s ON s.id=c.source_id
            WHERE c.source_id=@sourceId AND c.generation=@generation AND c.lease_id IS NOT NULL AND c.lease_until>@now AND s.status='active'
              AND NOT EXISTS(SELECT 1 FROM documents d WHERE d.id=c.container_id AND d.status IN ('deleted','purged','missing','removed_from_source'))
              AND NOT EXISTS(SELECT 1 FROM meta WHERE key='mutation_document_reservation:'||c.container_id)
            """, new { sourceId, generation, now = Format(clock.GetUtcNow()) }, null, token)) != 0;
    }
    public async ValueTask FailAsync(CalendarRefreshWork work, string error, int? retryAfterSeconds = null, string? rejectedHash = null, string? processingGeneration = null, CancellationToken token = default)
    {
        if (work.MaterializationOnly) { await FailMaterializationAsync(work, error, token); return; }
        if (!Errors.Contains(error) || rejectedHash is not null && (rejectedHash.Length != 64 || !rejectedHash.All(char.IsAsciiHexDigit)) || processingGeneration?.Length > 1024)
            throw new ArgumentException("Invalid calendar failure metadata.");
        await state.QueueWriteAsync(async (connection, transaction, ct) =>
        {
            var row = await connection.QuerySingleOrDefaultAsync<FailureRow>(Sql("SELECT c.poll_interval_s Interval,c.failure_count Failures FROM calendar_subscriptions c JOIN sources s ON s.id=c.source_id WHERE c.source_id=@SourceId AND c.generation=@Generation AND c.lease_id=@LeaseId AND s.status='active' AND NOT EXISTS(SELECT 1 FROM calendar_subscription_publications p WHERE p.source_id=c.source_id)", work, transaction, ct));
            if (row is null) return 0;
            var delay = Math.Min(86400d, 30d * Math.Pow(2, Math.Min(row.Failures, 12))) * (0.8 + Random.Shared.NextDouble() * 0.4);
            delay = Math.Min(86400, Math.Max(delay, Math.Clamp(retryAfterSeconds ?? 0, 0, 86400)));
            if (error is "calendar_unavailable" or "calendar_parse_failed" or "calendar_rejected_unchanged") delay = Math.Max(delay, row.Interval);
            await connection.ExecuteAsync(Sql("""
                UPDATE calendar_subscriptions SET error_code=@error,failure_count=MIN(failure_count+1,30),lease_id=NULL,lease_until=NULL,
                  next_attempt_at=CASE WHEN requested=1 THEN @now ELSE @next END,
                  rejected_hash=COALESCE(@rejectedHash,rejected_hash),rejected_generation=COALESCE(@processingGeneration,rejected_generation)
                WHERE source_id=@SourceId AND generation=@Generation AND lease_id=@LeaseId;
                """, new { work.SourceId, work.Generation, work.LeaseId, error, now = Format(clock.GetUtcNow()), next = Format(clock.GetUtcNow().AddSeconds(delay)), rejectedHash, processingGeneration }, transaction, ct));
            return 0;
        }, token);
    }
    public async ValueTask CompleteUnchangedAsync(CalendarRefreshWork work, CancellationToken token = default)
    {
        await state.QueueWriteAsync(async (connection, transaction, ct) =>
        {
            if (work.SnapshotHash is null) throw new CalendarSubscriptionException("calendar_snapshot_missing");
            await connection.ExecuteAsync(Sql("""
                UPDATE calendar_subscriptions SET last_success_at=@now,error_code=NULL,failure_count=0,lease_id=NULL,lease_until=NULL,
                  next_attempt_at=CASE WHEN requested=1 THEN @now ELSE @next END
                WHERE source_id=@SourceId AND generation=@Generation AND lease_id=@LeaseId AND snapshot_hash=@SnapshotHash
                  AND EXISTS(SELECT 1 FROM sources WHERE id=@SourceId AND status='active');
                """, new { work.SourceId, work.Generation, work.LeaseId, work.SnapshotHash, now = Format(clock.GetUtcNow()), next = Format(clock.GetUtcNow().AddSeconds(await connection.ExecuteScalarAsync<int>(Sql("SELECT poll_interval_s FROM calendar_subscriptions WHERE source_id=@SourceId", work, transaction, ct)))) }, transaction, ct));
            return 0;
        }, token);
    }

    private CalendarSubscription View(Row row)
    {
        var success = Parse(row.Success);
        var freshness = CalendarFreshness.State(row.Status, row.Error, success, row.Interval, clock.GetUtcNow());
        return new(row.Id, row.Name, row.Origin, row.Status, row.Interval, Parse(row.Created)!.Value, Parse(row.Attempt), success, Parse(row.Change), Parse(row.Next), row.Error, freshness, row.ContainerId);
    }
    private async Task CheckQueueCapacity(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, CancellationToken token)
    {
        var pending = await connection.ExecuteScalarAsync<long>(Sql("SELECT COALESCE((SELECT SUM(requested+CASE WHEN lease_id IS NULL THEN 0 ELSE 1 END) FROM calendar_subscriptions),0)+(SELECT COUNT(*) FROM jobs WHERE status IN ('queued','running'))", null, transaction, token));
        if (pending >= (options?.CurrentValue.Limits.Global.QueuedJobs ?? 10000)) throw new IngestionConflictException("capacity");
    }
    private const string Select = "SELECT s.id Id,s.name Name,s.status Status,s.created_at Created,c.origin Origin,c.container_id ContainerId,c.poll_interval_s Interval,c.last_attempt_at Attempt,c.last_success_at Success,c.last_change_at Change,c.next_attempt_at Next,c.error_code Error FROM calendar_subscriptions c JOIN sources s ON s.id=c.source_id";
    private static DateTimeOffset? Parse(string? value) => value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static CommandDefinition Sql(string sql, object? args, System.Data.Common.DbTransaction? transaction, CancellationToken token) => new(sql, args, transaction, cancellationToken: token);
    private sealed class Row { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Origin { get; set; } = ""; public string ContainerId { get; set; } = ""; public string Status { get; set; } = ""; public int Interval { get; set; } public string Created { get; set; } = ""; public string? Attempt { get; set; } public string? Success { get; set; } public string? Change { get; set; } public string? Next { get; set; } public string? Error { get; set; } }
    private sealed class WorkRow { public string SourceId { get; set; } = ""; public string ContainerId { get; set; } = ""; public long Generation { get; set; } public string ProtectedUrl { get; set; } = ""; public string? ETag { get; set; } public string? Modified { get; set; } public string? SnapshotHash { get; set; } public string? SnapshotPath { get; set; } public string? RejectedHash { get; set; } public string? RejectedGeneration { get; set; } public int Deep { get; set; } }
    private sealed class FailureRow { public int Interval { get; set; } public int Failures { get; set; } }
    private static readonly HashSet<string> Errors = new(StringComparer.Ordinal)
    {
        "calendar_timeout", "calendar_transport", "calendar_dns_failed", "calendar_staging_unavailable", "calendar_connect_failed", "calendar_source_changed", "calendar_origin_not_allowed", "calendar_address_refused", "calendar_canary_refused", "calendar_canary_invalid",
        "calendar_url_invalid", "calendar_origin_changed", "calendar_validator_invalid", "calendar_redirect_refused", "calendar_empty_response", "calendar_unavailable", "calendar_rate_limited", "calendar_server_error", "calendar_http_error",
        "calendar_encoded_bytes", "calendar_decoded_bytes", "calendar_encoding", "calendar_expansion_ratio", "calendar_compression_incomplete", "calendar_compression_trailing_bytes", "calendar_parse_failed", "calendar_rejected_unchanged", "calendar_snapshot_missing", "calendar_secret_invalid", "calendar_snapshot_invalid", "calendar_snapshot_unowned", "calendar_publication_failed", "calendar_capacity"
    };
}
