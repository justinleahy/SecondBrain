using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Durability;

namespace SecondBrain.Storage.Ingestion;

public sealed class SqliteChunkingWorkRepository(IStateStore state, IPublicationCoordinator publication,
    IDocumentRepository documents, IOptionsMonitor<SecondBrainOptions> options) : IChunkingWorkRepository
{
    private readonly SemaphoreSlim processing = new(1, 1);
    private string? lastProfile;
    private string afterDocument = "";
    public async ValueTask<int> ProcessAsync(ChunkingProfile profile, int limit = 16, CancellationToken cancellationToken = default)
    {
        await processing.WaitAsync(cancellationToken);
        try { return await ProcessBatchAsync(profile, limit, cancellationToken); }
        finally { processing.Release(); }
    }

    private async ValueTask<int> ProcessBatchAsync(ChunkingProfile profile, int limit, CancellationToken cancellationToken)
    {
        var target = profile.GenerationJson;
        if (lastProfile != target) { lastProfile = target; afterDocument = ""; }
        string[] ids;
        await using (var lease = await state.OpenReadConnectionAsync(cancellationToken))
        {
            async Task<string[]> ReadAsync() => (await lease.Connection.QueryAsync<string>(new CommandDefinition("""
                SELECT d.id FROM documents d JOIN publication_fences f ON f.document_id=d.id
                WHERE d.id>@afterDocument AND d.status IN ('indexed','indexed_partial') AND
                  (((COALESCE(json_extract(f.generation_json,'$.chunk_target_tokens'),512)<>@TargetTokens
                    OR COALESCE(json_extract(f.generation_json,'$.chunk_max_tokens'),1024)<>@MaxTokens
                    OR COALESCE(json_extract(f.generation_json,'$.chunk_overlap_tokens'),64)<>@OverlapTokens)
                    AND NOT EXISTS(SELECT 1 FROM jobs j WHERE j.document_id=d.id AND j.revision=d.revision AND j.kind='chunk' AND json_extract(j.payload_json,'$.profile')=@target AND j.status='failed'))
                    OR d.publish_state='publishing')
                  AND NOT EXISTS(SELECT 1 FROM meta WHERE key='mutation_document_reservation:'||d.id)
                  AND NOT EXISTS(SELECT 1 FROM calendar_batches calendar WHERE calendar.container_id=d.id)
                ORDER BY d.id LIMIT @limit
                """, new { target, afterDocument, profile.TargetTokens, profile.MaxTokens, profile.OverlapTokens, limit = Math.Clamp(limit, 1, 64) }, cancellationToken: cancellationToken))).ToArray();
            ids = await ReadAsync();
            if (ids.Length == 0 && afterDocument.Length != 0) { afterDocument = ""; ids = await ReadAsync(); }
        }
        if (ids.Length != 0) afterDocument = ids[^1];
        var count = 0;
        foreach (var id in ids)
        {
            PublicationFence? fence;
            using (await PublicationCoordinator.AcquireDocumentLockAsync(state, id, cancellationToken))
                fence = await RechunkAsync(id, profile, cancellationToken);
            if (fence is null) continue;
            // Publication must not reacquire the document lock while we hold it.
            if (await publication.PublishCurrentAsync(fence, cancellationToken) == PublicationOutcome.Published) count++;
        }
        return count;
    }

    private async ValueTask<PublicationFence?> RechunkAsync(string id, ChunkingProfile profile, CancellationToken token)
    {
        Current? current;
        await using (var lease = await state.OpenReadConnectionAsync(token))
            current = await lease.Connection.QuerySingleOrDefaultAsync<Current>(new CommandDefinition("""
                SELECT d.revision AS Revision,d.title AS Title,f.generation_json AS Generation,p.bytes AS Bytes
                FROM documents d JOIN publication_fences f ON f.document_id=d.id
                JOIN document_projections projection ON projection.document_id=d.id AND projection.revision=d.revision AND projection.generation_json=f.generation_json
                JOIN payloads p ON p.id=projection.payload_id AND p.redacted_at IS NULL
                WHERE d.id=@id AND d.status IN ('indexed','indexed_partial')
                  AND NOT EXISTS(SELECT 1 FROM meta WHERE key='mutation_document_reservation:'||d.id)
                  AND NOT EXISTS(SELECT 1 FROM calendar_batches calendar WHERE calendar.container_id=d.id)
                """, new { id }, cancellationToken: token));
        if (current is null) return null;
        var target = profile.ApplyToGeneration(current.Generation);
        if (ChunkingProfile.FromGeneration(current.Generation) == profile) return new(id, current.Revision, current.Generation);
        var previous = JsonSerializer.Deserialize<TextProjection>(current.Bytes)!;
        TextProjection projection;
        try
        {
            var chunks = (previous.Segments is { Count: > 0 } segments
                ? TranscriptTextProcessor.Process(id, current.Title, previous.LosslessText, segments, profile, previous.Tags, previous.Path)
                : profile.Process(id, current.Title, previous.LosslessText, previous.Tags, previous.Path)).Chunks;
            var extraction = previous.Chunks.Any(chunk => chunk.Page is not null) ? await documents.CachedExtractionAsync(id, token) : null;
            if (extraction?.LosslessText == previous.LosslessText && extraction.Pages is { Count: > 0 } pages)
                chunks = chunks.Select(chunk => chunk with { Page = pages.FirstOrDefault(page => chunk.EndOffset > page.Start && chunk.StartOffset < (long)page.Start + page.Length)?.Number }).ToArray();
            projection = previous with { Chunks = chunks };
        }
        catch (IngestionConflictException exception)
        {
            await state.QueueWriteAsync(async (connection, transaction, ct) =>
            {
                return await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO jobs(id,kind,document_id,revision,generation_json,account_epoch,status,error,payload_json,created_at,updated_at)
                    SELECT @job,'chunk',d.id,d.revision,@prior,COALESCE((SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch'),0),'failed',@error,@body,@now,@now
                    FROM documents d JOIN publication_fences f ON f.document_id=d.id
                    WHERE d.id=@id AND d.revision=@revision AND f.generation_json=@prior AND d.status IN ('indexed','indexed_partial')
                    """, new { id, revision = current.Revision, target, prior = current.Generation, error = exception.Code, body = JsonSerializer.Serialize(new { profile = profile.GenerationJson }), job = Ulid.NewUlid().ToString(), now = DateTimeOffset.UtcNow.ToString("O") }, transaction, cancellationToken: ct));
            }, token);
            return null;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(projection);
        using var publicationGate = await PublicationCoordinator.AcquirePublicationGateAsync(state, token);
        var changed = await state.QueueWriteAsync(async (connection, transaction, ct) =>
        {
            var pending = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM jobs WHERE status IN ('queued','running');", transaction: transaction, cancellationToken: ct));
            if (pending >= options.CurrentValue.Limits.Global.QueuedJobs) return false;
            var updated = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE documents SET publish_state='publishing',status='indexed_partial',error=NULL WHERE id=@id AND revision=@revision AND status IN ('indexed','indexed_partial')
                  AND EXISTS(SELECT 1 FROM publication_fences WHERE document_id=@id AND generation_json=@prior)
                  AND NOT EXISTS(SELECT 1 FROM meta WHERE key='mutation_document_reservation:'||@id);
                """, new { id, revision = current.Revision, prior = current.Generation }, transaction, cancellationToken: ct));
            if (updated != 1) return false;
            var payload = Ulid.NewUlid().ToString();
            var parameters = new { id, revision = current.Revision, target, payload, bytes, hash = Convert.ToHexStringLower(SHA256.HashData(bytes)), refs = JsonSerializer.Serialize(new[] { id }), now = DateTimeOffset.UtcNow.ToString("O"), job = Ulid.NewUlid().ToString() };
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES(@payload,@hash,@bytes,@refs,@now);
                INSERT INTO payload_refs(payload_id,referrer_kind,referrer_id) VALUES(@payload,'document-projection',@id);
                UPDATE document_projections SET payload_id=@payload,generation_json=@target WHERE document_id=@id AND revision=@revision;
                UPDATE publication_fences SET generation_json=@target WHERE document_id=@id;
                UPDATE jobs SET status='cancelled',lease_until=NULL,updated_at=@now WHERE document_id=@id AND status IN ('queued','running');
                INSERT INTO jobs(id,kind,document_id,revision,generation_json,account_epoch,status,created_at,updated_at)
                VALUES(@job,'embed',@id,@revision,@target,COALESCE((SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch'),0),'queued',@now,@now);
                """, parameters, transaction, cancellationToken: ct));
            return true;
        }, token);
        return changed ? new(id, current.Revision, target) : null;
    }

    private sealed record Current(long Revision, string Title, string Generation, byte[] Bytes);
}
