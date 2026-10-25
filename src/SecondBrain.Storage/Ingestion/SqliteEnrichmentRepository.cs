using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;

namespace SecondBrain.Storage.Ingestion;

public sealed partial class SqliteEnrichmentRepository(IStateStore state, IIndexStore index, IPublicationCoordinator publication,
    IOptionsMonitor<SecondBrainOptions> options, TimeProvider clock, IManagedDocumentFiles? files = null,
    IInputStager? stager = null, SecondBrain.Core.Extraction.IExtractorClient? extractor = null) : IEnrichmentRepository, IEnrichmentBatchRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string after = "";

    public Task<EnrichmentJob> RequestAsync(string documentId, IngestionAuthority actor, string fingerprint, CancellationToken token = default) =>
        state.QueueWriteAsync(async (c, t, ct) =>
        {
            RequirePolicy(fingerprint);
            await Authorize(c, t, actor, ct);
            var row = await Read(c, t, documentId, ct) ?? throw new IngestionConflictException("not-found");
            if (row.PublishState != "published") throw new IngestionConflictException("processing");
            if (row.Enrichment is not null && JsonSerializer.Deserialize<DocumentEnrichment>(row.Enrichment) is { WrittenBack: true } existingResult && existingResult.Fingerprint == fingerprint)
            {
                var completed = await c.ExecuteScalarAsync<string>(Sql("SELECT id FROM jobs WHERE document_id=@documentId AND kind='enrich' AND json_extract(payload_json,'$.fingerprint')=@fingerprint ORDER BY revision DESC,created_at DESC LIMIT 1", new { documentId, fingerprint }, t, ct));
                if (completed is not null) return new EnrichmentJob(completed, documentId, "completed");
            }
            var id = await Schedule(c, t, row, fingerprint, actor, true, ct);
            var status = await c.ExecuteScalarAsync<string>(Sql("SELECT status FROM jobs WHERE id=@id", new { id }, t, ct));
            if (status is "failed" or "cancelled" || status == "completed" && WantsWriteback(row))
            {
                await RequireQueueCapacity(c, t, ct);
                await c.ExecuteAsync(Sql("UPDATE jobs SET status='queued',attempts=0,run_after=NULL,error=NULL,payload_json=@payload WHERE id=@id",
                    new { id, payload = JsonSerializer.Serialize(new JobPayload(fingerprint, Stamp(row), true, actor, ""), Json) }, t, ct));
                status = "queued";
            }
            return new EnrichmentJob(id, documentId, status!);
        }, token).AsTask();

    public Task<EnrichmentWork?> ClaimAsync(string fingerprint, CancellationToken token = default) => ClaimCoreAsync(fingerprint, 0, token);
    public Task<EnrichmentWork?> ClaimBackfillAsync(string fingerprint, CancellationToken token = default) => ClaimCoreAsync(fingerprint, 1, token);
    public Task<EnrichmentWork?> ClaimInteractiveAsync(string fingerprint, CancellationToken token = default) => ClaimCoreAsync(fingerprint, 2, token);
    private Task<EnrichmentWork?> ClaimCoreAsync(string fingerprint, int queue, CancellationToken token) =>
        state.QueueWriteAsync(async (c, t, ct) =>
        {
            if (!PolicyCurrent(fingerprint)) return null;
            var now = AuthTime.Format(clock.GetUtcNow());
            await c.ExecuteAsync(Sql("UPDATE jobs SET status='cancelled',lease_until=NULL,error='configuration_changed' WHERE kind='enrich' AND status='queued' AND json_extract(payload_json,'$.fingerprint')<>@fingerprint", new { fingerprint }, t, ct));
            await c.ExecuteAsync(Sql("UPDATE jobs SET status='queued',lease_until=NULL WHERE kind='enrich' AND status='running' AND lease_until<@now AND json_extract(payload_json,'$.batchId') IS NULL", new { now }, t, ct));
            var job = await Next(c, t, fingerprint, now, queue, ct);
            if (job is null)
            {
                const string candidates = """
                    SELECT d.id FROM documents d JOIN publication_fences f ON f.document_id=d.id
                    JOIN document_projections dp ON dp.document_id=d.id AND dp.revision=d.revision AND dp.generation_json=f.generation_json
                    JOIN payloads p ON p.id=dp.payload_id AND p.redacted_at IS NULL
                    WHERE d.id>@after AND d.status IN ('indexed','indexed_partial') AND d.publish_state='published'
                      AND (COALESCE(json_extract(CAST(p.bytes AS TEXT),'$.Enrichment.Fingerprint'),'')<>@fingerprint
                        OR (@writeback=1 AND COALESCE(json_extract(CAST(p.bytes AS TEXT),'$.Enrichment.WrittenBack'),0)=0))
                    ORDER BY d.id LIMIT 16
                    """;
                var ids = (await c.QueryAsync<string>(Sql(candidates, new { after, fingerprint, writeback = options.CurrentValue.Enrichment.WriteBackFrontmatter }, t, ct))).ToArray();
                if (ids.Length == 0 && after.Length > 0)
                {
                    after = "";
                    ids = (await c.QueryAsync<string>(Sql(candidates, new { after, fingerprint, writeback = options.CurrentValue.Enrichment.WriteBackFrontmatter }, t, ct))).ToArray();
                }
                foreach (var id in ids)
                {
                    after = id;
                    var row = await Read(c, t, id, ct);
                    if (row is null) continue;
                    if (row.Enrichment is not null && JsonSerializer.Deserialize<DocumentEnrichment>(row.Enrichment)?.Fingerprint == fingerprint && !WantsWriteback(row)) continue;
                    var writebackOnly = WantsWriteback(row) && row.SourceStatus == "active"
                        && JsonSerializer.Deserialize<DocumentEnrichment>(row.Enrichment!)?.Fingerprint == fingerprint
                        && (row.SourceKind != "folder" || SourceAllows(row));
                    var authority = writebackOnly ? (Allowed: true, Actor: (IngestionAuthority?)null) : await AutoAuthority(c, t, row, ct);
                    if (!authority.Allowed) continue;
                    var existing = JobId(row, fingerprint);
                    var status = await c.ExecuteScalarAsync<string>(Sql("SELECT status FROM jobs WHERE id=@existing", new { existing }, t, ct));
                    if (status == "cancelled" || status == "completed" && WantsWriteback(row))
                    {
                        await RequireQueueCapacity(c, t, ct);
                        await c.ExecuteAsync(Sql("""
                            UPDATE jobs SET status='queued',attempts=0,run_after=NULL,error=NULL,payload_json=@payload
                            WHERE id=@existing AND (status='completed' OR json_extract(payload_json,'$.manual')=0)
                            """, new { existing, payload = JsonSerializer.Serialize(new JobPayload(fingerprint, Stamp(row), false, authority.Actor, "", writebackOnly), Json) }, t, ct));
                    }
                    if (status is not null) continue;
                    await Schedule(c, t, row, fingerprint, authority.Actor, false, ct, writebackOnly); break;
                }
                job = await Next(c, t, fingerprint, now, queue, ct);
            }
            if (job is null) return null;
            var current = await Read(c, t, job.DocumentId, ct);
            var payload = JsonSerializer.Deserialize<JobPayload>(job.Payload, Json)!;
            if (current is null || !await Valid(c, t, current, job, payload, fingerprint, ct))
            {
                if (await c.ExecuteScalarAsync<int>(Sql("SELECT COUNT(*) FROM mutations WHERE kind='write_back' AND id LIKE @prefix AND revision_before=@revision AND status='finalized'", new { prefix = job.Id + ":frontmatter:%", revision = job.Revision }, t, ct)) > 0)
                    await c.ExecuteAsync(Sql("UPDATE jobs SET status='completed',lease_until=NULL,error=NULL WHERE id=@id", new { id = job.Id }, t, ct));
                else await Cancel(c, t, job.Id, ct);
                return null;
            }
            var lease = Guid.NewGuid().ToString("N"); payload = payload with { Lease = lease };
            await c.ExecuteAsync(Sql("UPDATE jobs SET status='running',attempts=attempts+1,lease_until=@until,payload_json=@payload,updated_at=@now WHERE id=@id",
                new { id = job.Id, until = AuthTime.Format(clock.GetUtcNow().AddMinutes(3)), now, payload = JsonSerializer.Serialize(payload, Json) }, t, ct));
            var tags = JsonSerializer.Deserialize<string[]>(current.Tags) ?? [];
            var cached = current.Enrichment is null ? null : JsonSerializer.Deserialize<DocumentEnrichment>(current.Enrichment);
            return new EnrichmentWork(job.Id, lease, current.Id, current.Revision, current.Generation, fingerprint, Stamp(current), current.Title,
                current.Text, tags, payload.Actor, cached?.Fingerprint == fingerprint ? cached : null);
        }, token).AsTask();

    public Task<bool> CheckAsync(EnrichmentWork work, CancellationToken token = default) =>
        state.QueueWriteAsync(async (c, t, ct) => await Check(c, t, work, ct), token).AsTask();
    public Task<bool> WasCompletedAsync(EnrichmentWork work, CancellationToken token = default) => state.QueueWriteAsync(async(c,t,ct) =>
        await c.ExecuteScalarAsync<int>(Sql("SELECT COUNT(*) FROM jobs WHERE id=@id AND status='completed' AND json_extract(payload_json,'$.lease')=@lease",new {id=work.JobId,lease=work.Lease},t,ct))==1,token).AsTask();

    public async Task<bool> PublishAsync(EnrichmentWork work, DocumentEnrichment result, CancellationToken token = default)
    {
        EnrichmentPolicy.Validate(result);
        if (result.Fingerprint != work.Fingerprint) throw new ArgumentException("Enrichment fingerprint mismatch.");
        var canonical = new List<string>();
        await using (var read = await index.OpenReadConnectionAsync(token))
            foreach (var tag in result.Tags)
                canonical.Add(await read.Connection.ExecuteScalarAsync<string>(new CommandDefinition(
                    "SELECT tag FROM effective_tags WHERE tag=@tag COLLATE NOCASE ORDER BY tag LIMIT 1", new { tag }, cancellationToken: token)) ?? tag);
        result = result with { Tags = canonical.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        using (await PublicationCoordinator.AcquireDocumentLockAsync(state, work.DocumentId, token))
        using (await PublicationCoordinator.AcquirePublicationGateAsync(state, token))
        {
            var saved = await state.QueueWriteAsync(async (c, t, ct) =>
            {
                if (!await Check(c, t, work, ct)) return false;
                var row = await c.QuerySingleAsync<ProjectionRow>(Sql("""
                    SELECT dp.payload_id Id,p.bytes Bytes FROM document_projections dp JOIN payloads p ON p.id=dp.payload_id AND p.redacted_at IS NULL
                    WHERE dp.document_id=@id AND dp.revision=@revision AND dp.generation_json=@generation
                    """, new { id = work.DocumentId, revision = work.Revision, generation = work.GenerationJson }, t, ct));
                var projection = JsonSerializer.Deserialize<TextProjection>(row.Bytes)!;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(projection with { Enrichment = result });
                var id = Ulid.NewUlid().ToString();
                await c.ExecuteAsync(Sql("""
                    INSERT INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES(@id,@hash,@bytes,@refs,@now);
                    INSERT INTO payload_refs(payload_id,referrer_kind,referrer_id) VALUES(@id,'document-projection',@document);
                    UPDATE document_projections SET payload_id=@id WHERE document_id=@document;
                    DELETE FROM payload_refs WHERE payload_id=@old AND referrer_kind='document-projection' AND referrer_id=@document;
                    UPDATE documents SET publish_state='publishing' WHERE id=@document;
                    """, new { id, hash = Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes, refs = JsonSerializer.Serialize(new[] { work.DocumentId }),
                        now = AuthTime.Format(clock.GetUtcNow()), document = work.DocumentId, old = row.Id }, t, ct));
                return true;
            }, token);
            if (!saved) { await FailAsync(work, "superseded", false, token); return false; }
        }
        var outcome = await publication.PublishCurrentAsync(new(work.DocumentId, work.Revision, work.GenerationJson), token);
        if (outcome == PublicationOutcome.Published && options.CurrentValue.Enrichment.WriteBackFrontmatter && !result.WrittenBack)
            await WriteBackAsync(work, result, token);
        await state.QueueWriteAsync(async (c, t, ct) => await c.ExecuteAsync(Sql("""
            UPDATE jobs SET status=@status,lease_until=NULL,error=NULL,updated_at=@now
            WHERE id=@id AND json_extract(payload_json,'$.lease')=@lease
            """, new { id = work.JobId, lease = work.Lease, status = outcome == PublicationOutcome.Published ? "completed" : "cancelled", now = AuthTime.Format(clock.GetUtcNow()) }, t, ct)), token);
        return outcome == PublicationOutcome.Published;
    }

    public Task FailAsync(EnrichmentWork work, string reason, bool retryable, CancellationToken token = default) =>
        state.QueueWriteAsync(async (c, t, ct) =>
        {
            var attempts = await c.ExecuteScalarAsync<int>(Sql("SELECT attempts FROM jobs WHERE id=@id", new { id = work.JobId }, t, ct));
            var retry = retryable && attempts < 5;
            var status = retry ? "queued" : reason is "superseded" or "configuration_changed" or "authority_changed" ? "cancelled" : "failed";
            return await c.ExecuteAsync(Sql("""
                UPDATE jobs SET status=@status,run_after=@after,lease_until=NULL,error=@reason,updated_at=@now
                WHERE id=@id AND status='running' AND lease_until>@now AND json_extract(payload_json,'$.lease')=@lease
                """, new { id = work.JobId, lease = work.Lease, status, reason,
                    after = retry ? AuthTime.Format(clock.GetUtcNow().AddSeconds(reason == "budget" ? 900 : Math.Min(3600, 30 * Math.Pow(2, attempts)))) : null,
                    now = AuthTime.Format(clock.GetUtcNow()) }, t, ct));
        }, token).AsTask();

    private async Task<bool> Check(DbConnection c, DbTransaction t, EnrichmentWork work, CancellationToken ct)
    {
        if (!PolicyCurrent(work.Fingerprint)) return false;
        var job = await c.QuerySingleOrDefaultAsync<JobRow>(Sql("SELECT id Id,document_id DocumentId,revision Revision,generation_json Generation,payload_json Payload,status Status FROM jobs WHERE id=@id AND status='running' AND lease_until>@now", new { id = work.JobId, now = AuthTime.Format(clock.GetUtcNow()) }, t, ct));
        if (job is null) return false;
        var payload = JsonSerializer.Deserialize<JobPayload>(job.Payload, Json)!;
        if (payload.Lease != work.Lease) return false;
        var row = await Read(c, t, work.DocumentId, ct);
        return row is not null && await Valid(c, t, row, job, payload, work.Fingerprint, ct);
    }
    private async Task<bool> Valid(DbConnection c, DbTransaction t, DocumentRow row, JobRow job, JobPayload payload, string fingerprint, CancellationToken ct)
    {
        if (row.Id != job.DocumentId || row.Revision != job.Revision || row.Generation != job.Generation || Stamp(row) != payload.SourceStamp || payload.Fingerprint != fingerprint) return false;
        if (payload.Actor is { } actor)
        {
            try { await Authorize(c, t, actor, ct); } catch (AuthorityChangedException) { return false; }
        }
        return payload.Manual || payload.Actor is not null || SourceAllows(row)
            || payload.WritebackOnly && row.SourceStatus == "active" && WantsWriteback(row)
                && JsonSerializer.Deserialize<DocumentEnrichment>(row.Enrichment!)?.Fingerprint == fingerprint;
    }
    private async Task<(bool Allowed, IngestionAuthority? Actor)> AutoAuthority(DbConnection c, DbTransaction t, DocumentRow row, CancellationToken ct)
    {
        if (row.SourceKind is "folder" or "calendar_subscription") return (SourceAllows(row), null);
        var actor = await c.QuerySingleOrDefaultAsync<ActorRow>(Sql("""
            SELECT c.id Id,c.generation Generation,c.account_epoch Epoch,c.name Name,c.kind Kind
            FROM jobs j JOIN credentials c ON c.id=j.initiating_credential_id AND c.generation=j.credential_generation AND c.account_epoch=j.account_epoch
            WHERE j.kind<>'enrich' AND ((j.document_id=@id AND j.revision=@revision) OR
              (j.document_id=(SELECT container_document_id FROM documents WHERE id=@id) AND
               j.revision=(SELECT p.revision FROM documents d JOIN documents p ON p.id=d.container_document_id WHERE d.id=@id)))
            ORDER BY (j.document_id=@id) DESC,j.created_at DESC LIMIT 1
            """, new { id = row.Id, revision = row.Revision }, t, ct));
        if (actor is null) return (false, null);
        var authority = new IngestionAuthority(actor.Id, actor.Generation, actor.Epoch, actor.Name, actor.Kind);
        try { await Authorize(c, t, authority, ct); return (true, authority); } catch (AuthorityChangedException) { return (false, null); }
    }
    private async Task<string> Schedule(DbConnection c, DbTransaction t, DocumentRow row, string fingerprint, IngestionAuthority? actor, bool manual, CancellationToken ct, bool writebackOnly = false)
    {
        var id = JobId(row, fingerprint);
        if (await c.ExecuteScalarAsync<int>(Sql("SELECT COUNT(*) FROM jobs WHERE id=@id", new { id }, t, ct)) != 0) return id;
        await RequireQueueCapacity(c, t, ct);
        await c.ExecuteAsync(Sql("""
            INSERT INTO jobs(id,kind,document_id,revision,generation_json,initiating_credential_id,credential_generation,account_epoch,payload_json,status,created_at,updated_at)
            VALUES(@id,'enrich',@document,@revision,@generation,@actor,@credentialGeneration,@epoch,@payload,'queued',@now,@now)
            """, new { id, document = row.Id, revision = row.Revision, generation = row.Generation, actor = actor?.CredentialId,
                credentialGeneration = actor?.CredentialGeneration, epoch = actor?.AccountEpoch ?? await c.ExecuteScalarAsync<long>(Sql("SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch'", null, t, ct)),
                payload = JsonSerializer.Serialize(new JobPayload(fingerprint, Stamp(row), manual, actor, "", writebackOnly), Json), now = AuthTime.Format(clock.GetUtcNow()) }, t, ct));
        return id;
    }
    private async Task RequireQueueCapacity(DbConnection c, DbTransaction t, CancellationToken ct)
    {
        if (await c.ExecuteScalarAsync<long>(Sql("SELECT COUNT(*) FROM jobs WHERE status IN ('queued','running')", null, t, ct)) >= options.CurrentValue.Limits.Global.QueuedJobs)
            throw new IngestionConflictException("capacity");
    }
    private static Task<JobRow?> Next(DbConnection c, DbTransaction t, string fingerprint, string now, int queue, CancellationToken ct) =>
        c.QuerySingleOrDefaultAsync<JobRow>(Sql("""
            SELECT id Id,document_id DocumentId,revision Revision,generation_json Generation,payload_json Payload,status Status
            FROM jobs WHERE kind='enrich' AND status='queued' AND (run_after IS NULL OR run_after<=@now)
              AND json_extract(payload_json,'$.fingerprint')=@fingerprint
              AND (@queue=0 OR (@queue=1 AND COALESCE(json_extract(payload_json,'$.manual'),0)=0)
                   OR (@queue=2 AND json_extract(payload_json,'$.manual')=1))
              AND json_extract(payload_json,'$.batchId') IS NULL ORDER BY created_at,id LIMIT 1
            """, new { fingerprint, now, queue }, t, ct));
    private static Task<DocumentRow?> Read(DbConnection c, DbTransaction t, string id, CancellationToken ct) =>
        c.QuerySingleOrDefaultAsync<DocumentRow>(Sql($$"""
            SELECT d.id Id,d.revision Revision,d.title Title,d.publish_state PublishState,f.generation_json Generation,s.kind SourceKind,s.status SourceStatus,
              COALESCE(s.config_json,'{}') SourceConfig,s.id SourceId,
              substr(json_extract(CAST(p.bytes AS TEXT),'$.lossless_text'),1,20000) Text,
              COALESCE(json_extract(CAST(p.bytes AS TEXT),'$.Tags'),'[]') Tags,
              json_extract(CAST(p.bytes AS TEXT),'$.Path') Path,
              json_extract(CAST(p.bytes AS TEXT),'$.Enrichment') Enrichment
            FROM documents d JOIN publication_fences f ON f.document_id=d.id JOIN sources s ON s.id=d.source_id
            JOIN document_projections dp ON dp.document_id=d.id AND dp.revision=d.revision AND dp.generation_json=f.generation_json
            JOIN payloads p ON p.id=dp.payload_id AND p.redacted_at IS NULL
            WHERE d.id=@id AND d.status IN ('indexed','indexed_partial')
              AND length(trim(json_extract(CAST(p.bytes AS TEXT),'$.lossless_text')))>0
              AND NOT EXISTS(SELECT 1 FROM documents parent WHERE parent.id=d.container_document_id AND parent.status IN ({{DocumentStatusSql.Unavailable}}))
              AND NOT EXISTS(SELECT 1 FROM meta WHERE key='mutation_document_reservation:'||d.id)
              AND NOT EXISTS(SELECT 1 FROM calendar_batches WHERE container_id=d.id)
            """, new { id }, t, ct));
    private static bool SourceAllows(DocumentRow row)
    {
        if (row.SourceStatus != "active") return false;
        if (row.SourceKind == "calendar_subscription") return true;
        using var config = JsonDocument.Parse(row.SourceConfig);
        return config.RootElement.TryGetProperty("enrich", out var enabled) && enabled.ValueKind == JsonValueKind.True;
    }
    private static string Stamp(DocumentRow row) => ContentTypeSelection.Hash(row.SourceId + row.SourceStatus + row.SourceConfig);
    private static string JobId(DocumentRow row, string fingerprint) => "enrich-" + ContentTypeSelection.Hash(row.Id + row.Revision + row.Generation + Stamp(row) + fingerprint);
    private bool PolicyCurrent(string fingerprint) => options.CurrentValue.Enrichment.Enabled && EnrichmentPolicy.Fingerprint(options.CurrentValue) == fingerprint;
    private void RequirePolicy(string fingerprint) { if (!PolicyCurrent(fingerprint)) throw new IngestionConflictException("enrichment-disabled"); }
    private async Task Authorize(DbConnection c, DbTransaction t, IngestionAuthority actor, CancellationToken ct)
    {
        await IngestionAuthorityValidator.ValidateAsync(c, t, actor, Scope.Read, clock.GetUtcNow(), cancellationToken: ct);
        await IngestionAuthorityValidator.ValidateAsync(c, t, actor, Scope.Infer, clock.GetUtcNow(), cancellationToken: ct);
    }
    private static Task Cancel(DbConnection c, DbTransaction t, string id, CancellationToken ct) => c.ExecuteAsync(Sql("UPDATE jobs SET status='cancelled',lease_until=NULL,error='superseded' WHERE id=@id", new { id }, t, ct));
    private static CommandDefinition Sql(string sql, object? parameters, DbTransaction transaction, CancellationToken token) => new(sql, parameters, transaction, cancellationToken: token);
    private sealed record JobPayload(string Fingerprint, string SourceStamp, bool Manual, IngestionAuthority? Actor, string Lease, bool WritebackOnly = false);
    private sealed class ActorRow { public string Id { get; set; } = ""; public long Generation { get; set; } public long Epoch { get; set; } public string Name { get; set; } = ""; public string Kind { get; set; } = ""; }
    private sealed class JobRow { public string Id { get; set; } = ""; public string DocumentId { get; set; } = ""; public long Revision { get; set; } public string Generation { get; set; } = ""; public string Payload { get; set; } = ""; public string Status { get; set; } = ""; }
    private sealed class ProjectionRow { public string Id { get; set; } = ""; public byte[] Bytes { get; set; } = []; }
    private sealed class DocumentRow { public string Id { get; set; } = ""; public long Revision { get; set; } public string Generation { get; set; } = ""; public string Title { get; set; } = ""; public string Text { get; set; } = ""; public string Tags { get; set; } = "[]"; public string? Path { get; set; } public string? Enrichment { get; set; } public string PublishState { get; set; } = ""; public string SourceKind { get; set; } = ""; public string SourceStatus { get; set; } = ""; public string SourceConfig { get; set; } = "{}"; public string SourceId { get; set; } = ""; }
}
