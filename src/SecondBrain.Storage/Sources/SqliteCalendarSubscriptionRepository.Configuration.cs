using System.Text.Json;
using Dapper;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Storage.Durability;

namespace SecondBrain.Storage.Sources;

public sealed partial class SqliteCalendarSubscriptionRepository
{
    public async ValueTask<IReadOnlyList<ConfiguredCalendarState>> ListConfiguredAsync(CancellationToken token = default)
    {
        await using var lease = await state.OpenReadConnectionAsync(token);
        return (await lease.Connection.QueryAsync<ConfiguredCalendarState>(Sql("""
            SELECT s.id Id,c.protected_url ProtectedUrl FROM sources s JOIN calendar_subscriptions c ON c.source_id=s.id
            WHERE json_extract(s.config_json,'$.configured')=1 AND s.status<>'removed' ORDER BY s.id
            """, null, null, token))).ToArray();
    }

    public async ValueTask ReconcileConfiguredAsync(IReadOnlyList<ConfiguredCalendarRegistration> registrations, CancellationToken token = default)
    {
        if (registrations.Count > 128 || registrations.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != registrations.Count)
            throw new CalendarSubscriptionException("calendar_settings_invalid");
        foreach (var item in registrations)
        {
            if (!CalendarSubscriptionOptions.IsId(item.Id) || string.IsNullOrEmpty(item.ProtectedUrl) || item.ProtectedUrl.Length > 32768 || Uri.TryCreate(item.ProtectedUrl, UriKind.Absolute, out _))
                throw new CalendarSubscriptionException("calendar_settings_invalid");
            CalendarSubscriptionService.Validate(item.Name, item.Interval); _ = CalendarLink.ValidateOrigin(item.Origin);
        }
        var ids = registrations.Select(item => item.Id).ToArray();
        while (true)
        {
            using var gate = await PublicationCoordinator.AcquirePublicationGateAsync(state, token);
            string? pending;
            await using (var lease = await state.OpenReadConnectionAsync(token))
                pending = await lease.Connection.ExecuteScalarAsync<string?>(Sql("""
                    SELECT b.container_id FROM calendar_batches b JOIN sources s ON s.id=b.source_id
                    WHERE b.phase='publishing' AND (json_extract(s.config_json,'$.configured')=1 OR s.id IN @ids) LIMIT 1
                    """, new { ids }, null, token));
            if (pending is not null)
            {
                gate.Dispose();
                if (publications is null || await publications.RecoverCalendarAsync(pending, token) != PublicationOutcome.Published)
                    throw new IngestionConflictException("calendar-publication-pending");
                continue;
            }
            await state.QueueWriteAsync(async (connection, transaction, ct) =>
            {
                // All conflicts are checked before any entry changes. Failed startup cannot partially register a batch.
                var existing = (await connection.QueryAsync<ConfiguredRow>(Sql("""
                    SELECT s.id Id,s.kind Kind,s.status Status,s.name Name,COALESCE(json_extract(s.config_json,'$.configured'),0) Configured,
                      c.protected_url ProtectedUrl,c.poll_interval_s Interval,
                      CASE WHEN EXISTS(SELECT 1 FROM calendar_lifecycle_pauses p WHERE p.source_id=s.id)
                        OR EXISTS(SELECT 1 FROM documents d WHERE d.id=c.container_id AND d.status IN ('deleted','purged','missing','removed_from_source'))
                        THEN 1 ELSE 0 END LifecyclePaused
                    FROM sources s LEFT JOIN calendar_subscriptions c ON c.source_id=s.id
                    WHERE json_extract(s.config_json,'$.configured')=1 OR s.id IN @ids
                    """, new { ids }, transaction, ct))).ToDictionary(item => item.Id, StringComparer.Ordinal);
                foreach (var item in registrations)
                {
                    if (existing.TryGetValue(item.Id, out var prior))
                    {
                        if (prior.Kind != "calendar_subscription" || prior.Configured != 1 || prior.Status != "removed" && (prior.ProtectedUrl != item.ExpectedProtectedUrl || item.ProtectedUrl != prior.ProtectedUrl))
                            throw new IngestionConflictException("calendar-configured-conflict");
                    }
                    else if (item.ExpectedProtectedUrl is not null) throw new IngestionConflictException("calendar-configured-conflict");
                }
                var now = Format(clock.GetUtcNow());
                foreach (var item in registrations)
                {
                    existing.TryGetValue(item.Id, out var prior);
                    var enabled = item.Enabled && prior?.LifecyclePaused != 1;
                    var active = enabled ? "active" : "disabled";
                    if (prior is null)
                    {
                        if (item.Enabled) await CheckQueueCapacity(connection, transaction, ct);
                        await connection.ExecuteAsync(Sql("""
                            INSERT INTO sources(id,kind,name,config_json,status,created_at) VALUES(@Id,'calendar_subscription',@Name,@config,@status,@now);
                            INSERT INTO calendar_subscriptions(source_id,container_id,origin,protected_url,poll_interval_s,requested,next_attempt_at)
                            VALUES(@Id,@container,@Origin,@ProtectedUrl,@Interval,@requested,@next);
                            """, new { item.Id, item.Name, config = JsonSerializer.Serialize(new { origin = item.Origin, poll_interval_s = item.Interval, configured = true }),
                                status = active, now, container = Ulid.NewUlid().ToString(), item.Origin, item.ProtectedUrl, item.Interval,
                                requested = item.Enabled ? 1 : 0, next = item.Enabled ? now : null }, transaction, ct));
                    }
                    else if (prior.Status != "removed" && (prior.Status != active || prior.Name != item.Name || prior.Interval != item.Interval))
                    {
                        if (enabled && await connection.ExecuteScalarAsync<long>(Sql("SELECT requested+CASE WHEN lease_id IS NULL THEN 0 ELSE 1 END FROM calendar_subscriptions WHERE source_id=@Id", item, transaction, ct)) == 0)
                            await CheckQueueCapacity(connection, transaction, ct);
                        await connection.ExecuteAsync(Sql("""
                            UPDATE sources SET name=@Name,status=@status,config_json=json_set(config_json,'$.poll_interval_s',@Interval) WHERE id=@Id;
                            UPDATE calendar_subscriptions SET poll_interval_s=@Interval,generation=generation+1,lease_id=NULL,lease_until=NULL,
                              requested=@requested,deep_requested=0,next_attempt_at=@next WHERE source_id=@Id;
                            """, new { item.Id, item.Name, item.Interval, status = active, requested = enabled ? 1 : 0, next = enabled ? now : null }, transaction, ct));
                    }
                }
                // Omitting a configured entry stops network work without hiding the retained events.
                foreach (var omitted in existing.Values.Where(item => item.Configured == 1 && item.Status == "active" && !ids.Contains(item.Id, StringComparer.Ordinal)))
                    await connection.ExecuteAsync(Sql("""
                        UPDATE sources SET status='disabled' WHERE id=@Id;
                        UPDATE calendar_subscriptions SET generation=generation+1,lease_id=NULL,lease_until=NULL,requested=0,deep_requested=0,next_attempt_at=NULL WHERE source_id=@Id;
                        """, omitted, transaction, ct));
                return 0;
            }, token);
            return;
        }
    }
    private sealed class ConfiguredRow
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Status { get; set; } = "";
        public string Name { get; set; } = "";
        public int Configured { get; set; }
        public string? ProtectedUrl { get; set; }
        public int Interval { get; set; }
        public int LifecyclePaused { get; set; }
    }
}
