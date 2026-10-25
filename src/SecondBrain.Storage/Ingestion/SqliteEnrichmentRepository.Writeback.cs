using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Storage.Ingestion;

public sealed partial class SqliteEnrichmentRepository
{
    private bool ManagedMarkdown(string id, string? path) => files is not null && path is not null
        && Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown"
        && (path == files.GetManagedPath(id, Path.GetExtension(path)) || path == files.GetAssistantNotePath(id));

    private bool WantsWriteback(DocumentRow row) => options.CurrentValue.Enrichment.WriteBackFrontmatter
        && ManagedMarkdown(row.Id, row.Path) && row.Enrichment is not null
        && JsonSerializer.Deserialize<DocumentEnrichment>(row.Enrichment) is { WrittenBack: false };

    private async Task WriteBackAsync(EnrichmentWork work, DocumentEnrichment enrichment, CancellationToken token)
    {
        WritebackRow? row;
        await using (var read = await state.OpenReadConnectionAsync(token))
            row = await read.Connection.QuerySingleOrDefaultAsync<WritebackRow>(new CommandDefinition("""
                SELECT d.title Title,d.type Type,d.type_origin TypeOrigin,d.props_json Props,d.props_original_json OriginalProps,
                  d.status Status,d.content_hash Hash,d.mime Mime,d.source_url SourceUrl,p.bytes Projection
                FROM documents d JOIN document_projections dp ON dp.document_id=d.id AND dp.revision=d.revision
                JOIN payloads p ON p.id=dp.payload_id AND p.redacted_at IS NULL WHERE d.id=@id AND d.revision=@revision
                """, new { id = work.DocumentId, revision = work.Revision }, cancellationToken: token));
        if (row is null) return;
        var originalProjection = JsonSerializer.Deserialize<TextProjection>(row.Projection)!;
        var path = originalProjection.Path;
        if (!ManagedMarkdown(work.DocumentId, path)) return;
        if (stager is null || extractor is null) throw new InvalidOperationException("Write-back services are unavailable.");
        await using var original = await files!.OpenReadAsync(path!, token);
        await using var stage = await stager.StageAsync(original, Path.GetFileName(path!), token);
        if (stage.Length > 10 * 1024 * 1024 || stage.Sha256 != row.Hash) throw new InvalidDataException("frontmatter-file-changed");
        var inspected = await extractor.ExtractAsync(stage.Content, Path.GetExtension(path!), token);
        stage.Content.Position = 0;
        using var captured = new MemoryStream(); await stage.Content.CopyToAsync(captured, token);
        var bytes = EnrichmentFrontmatter.Apply(captured.ToArray(), inspected, enrichment);
        var profile = ChunkingProfile.FromGeneration(work.GenerationJson);
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff');
        await using (var verification = await stager.StageAsync(new MemoryStream(bytes, writable: false), Path.GetFileName(path!), token))
        {
            var checkedOutput = await extractor.ExtractAsync(verification.Content, Path.GetExtension(path!), token);
            if (checkedOutput.Status != "extracted" || checkedOutput.PhysicalFormat != "markdown" || checkedOutput.LosslessText != text)
                throw new InvalidDataException("frontmatter-writeback-validation");
        }
        var projection = profile.Process(work.DocumentId, row.Title, text, originalProjection.Tags, path) with
        {
            Aliases = originalProjection.Aliases, Links = originalProjection.Links,
            SourceCreatedAt = originalProjection.SourceCreatedAt, SourceUpdatedAt = originalProjection.SourceUpdatedAt,
            Enrichment = enrichment with { WrittenBack = true }
        };
        var mutation = work.JobId + ":frontmatter:" + work.Lease;
        var payload = mutation + ":projection";
        var projectionBytes = JsonSerializer.SerializeToUtf8Bytes(projection);
        var ready = await state.QueueWriteAsync(async (c, t, ct) =>
        {
            if (!await Check(c, t, work, ct) || !options.CurrentValue.Enrichment.WriteBackFrontmatter) return false;
            await c.ExecuteAsync(Sql("""
                INSERT OR IGNORE INTO payloads(id,sha256,bytes,document_refs_json,created_at) VALUES(@payload,@hash,@bytes,@refs,@now);
                """, new { payload, hash = Convert.ToHexStringLower(SHA256.HashData(projectionBytes)), bytes = projectionBytes,
                    refs = JsonSerializer.Serialize(new[] { work.DocumentId }), now = AuthTime.Format(clock.GetUtcNow()) }, t, ct));
            return true;
        }, token);
        if (!ready) return;
        var finalization = new MutationFinalization
        {
            MutationIdentity = mutation, AssistantNote = path == files.GetAssistantNotePath(work.DocumentId),
            DocumentId = work.DocumentId, RevisionBefore = work.Revision, RevisionAfter = work.Revision + 1,
            RevisionCause = "enrichment", GenerationJson = work.GenerationJson,
            EnrichmentWriteback = new(work.JobId, work.Lease, work.Fingerprint, work.SourceStamp),
            DocumentMetadata = new(row.Title, row.Type, row.TypeOrigin, row.Props, row.OriginalProps, row.Status, payload, row.Mime, row.SourceUrl)
        };
        await files.WriteRevisionAsync(work.DocumentId, bytes, Path.GetExtension(path!), row.Hash, finalization, token);
        await publication.PublishCurrentAsync(new(work.DocumentId, work.Revision + 1, work.GenerationJson), token);
    }

    private sealed class WritebackRow
    {
        public string Title { get; set; } = "";
        public string Type { get; set; } = "";
        public string TypeOrigin { get; set; } = "";
        public string Props { get; set; } = "{}";
        public string OriginalProps { get; set; } = "{}";
        public string Status { get; set; } = "";
        public string Hash { get; set; } = "";
        public string? Mime { get; set; }
        public string? SourceUrl { get; set; }
        public byte[] Projection { get; set; } = [];
    }
}
