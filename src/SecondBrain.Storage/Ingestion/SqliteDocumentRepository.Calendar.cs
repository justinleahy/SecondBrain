using System.Text.Json;
using Dapper;
using SecondBrain.Extraction.Contracts;

namespace SecondBrain.Storage.Ingestion;

public sealed partial class SqliteDocumentRepository
{
    public async ValueTask<int> ScheduleCalendarMaterializationAsync(CalendarWindow window, CancellationToken cancellationToken = default)
    {
        var range = window.Validate();
        window = new(range.From.ToString("O"), range.To.ToString("O"), range.Zone.Id, window.MaxOccurrences);
        using var gate = await SecondBrain.Storage.Durability.PublicationCoordinator.AcquirePublicationGateAsync(state, cancellationToken);
        return await state.QueueWriteAsync(async (connection, transaction, token) =>
        {
            var queued = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM jobs WHERE status IN ('queued','running')", transaction: transaction, cancellationToken: token));
            var room = Math.Clamp(((optionsMonitor?.CurrentValue ?? options?.Value)?.Limits.Global.QueuedJobs ?? 10000) - queued, 0, 32);
            if (room == 0) return 0;
            var rows = (await connection.QueryAsync<CalendarDue>(new CommandDefinition("""
                SELECT d.id Id,d.revision Revision,f.generation_json Generation,p.bytes Bytes
                FROM calendar_coverage c JOIN documents d ON d.id=c.container_id JOIN sources s ON s.id=d.source_id
                JOIN publication_fences f ON f.document_id=d.id JOIN document_projections dp ON dp.document_id=d.id JOIN payloads p ON p.id=dp.payload_id
                WHERE (c.covered_from<>@from OR c.covered_to<>@to OR c.time_zone<>@zone OR EXISTS(SELECT 1 FROM calendar_batches b WHERE b.container_id=d.id AND b.phase='needs_materialization'))
                  AND s.kind<>'calendar_subscription' AND d.status IN ('indexed','indexed_partial') AND p.redacted_at IS NULL
                  AND (s.kind NOT IN ('folder','calendar_subscription') OR s.status='active')
                  AND NOT EXISTS(SELECT 1 FROM jobs j WHERE j.document_id=d.id AND j.kind='extract' AND j.status IN ('queued','running'))
                ORDER BY c.covered_to,d.id LIMIT @limit
                """, new { from = window.From, to = window.To, zone = window.TimeZone, limit = room }, transaction, cancellationToken: token))).ToArray();
            foreach (var row in rows)
            {
                var projection = JsonSerializer.Deserialize<SecondBrain.Core.Ingestion.TextProjection>(row.Bytes)!;
                if (projection.Path is null) continue;
                var now = DateTimeOffset.UtcNow.ToString("O");
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO jobs(id,kind,document_id,revision,generation_json,account_epoch,status,payload_json,created_at,updated_at)
                    VALUES(@job,'extract',@id,@revision,@generation,COALESCE((SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch'),0),'queued',@payload,@now,@now);
                    UPDATE documents SET status='processing' WHERE id=@id;
                    """, new { job = Ulid.NewUlid().ToString(), id = row.Id, revision = row.Revision, generation = row.Generation,
                    payload = JsonSerializer.Serialize(new { path = projection.Path, file_name = Path.GetFileName(projection.Path), calendar_window = window }), now }, transaction, cancellationToken: token));
            }
            return rows.Length;
        }, cancellationToken);
    }
    private sealed class CalendarDue { public string Id { get; set; } = ""; public long Revision { get; set; } public string Generation { get; set; } = ""; public byte[] Bytes { get; set; } = []; }
}
