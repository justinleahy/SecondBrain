using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Auth;

namespace SecondBrain.Storage.Durability;

internal static class EnrichmentWritebackConditions
{
    public static async Task ValidateAsync(DbConnection c, DbTransaction t, MutationFinalization finalization,
        IOptionsMonitor<SecondBrainOptions>? options, DateTimeOffset now, CancellationToken token)
    {
        if (finalization.EnrichmentWriteback is not { } guard) return;
        var current = options?.CurrentValue;
        if (current is null || !current.Enrichment.Enabled || !current.Enrichment.WriteBackFrontmatter
            || EnrichmentPolicy.Fingerprint(current) != guard.Fingerprint)
            throw new InvalidOperationException("Enrichment write-back is no longer authorized.");
        var payload = await c.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT j.payload_json FROM jobs j JOIN documents d ON d.id=j.document_id
            JOIN publication_fences f ON f.document_id=d.id JOIN sources s ON s.id=d.source_id
            WHERE j.id=@JobId AND j.kind='enrich' AND j.status='running' AND j.lease_until>@now
              AND j.document_id=@document AND j.revision=@revision AND d.revision=j.revision AND f.generation_json=j.generation_json
              AND json_extract(j.payload_json,'$.lease')=@Lease AND json_extract(j.payload_json,'$.fingerprint')=@Fingerprint
            """, new { guard.JobId, guard.Lease, guard.Fingerprint, document = finalization.DocumentId, revision = finalization.RevisionBefore, now = AuthTime.Format(now) }, t, cancellationToken: token));
        if (payload is null) throw new InvalidOperationException("Enrichment write-back job changed.");
        var source = await c.QuerySingleAsync<(string Id, string Status, string Config)>(new CommandDefinition("""
            SELECT s.id,s.status,COALESCE(s.config_json,'{}') FROM sources s JOIN documents d ON d.source_id=s.id WHERE d.id=@id
            """, new { id = finalization.DocumentId }, t, cancellationToken: token));
        if (ContentTypeSelection.Hash(source.Id + source.Status + source.Config) != guard.SourceStamp)
            throw new InvalidOperationException("Enrichment write-back source changed.");
        using var parsed = JsonDocument.Parse(payload);
        if (parsed.RootElement.TryGetProperty("actor", out var value) && value.ValueKind == JsonValueKind.Object)
        {
            var actor = value.Deserialize<IngestionAuthority>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            await IngestionAuthorityValidator.ValidateAsync(c, t, actor, Scope.Read, now, cancellationToken: token);
            await IngestionAuthorityValidator.ValidateAsync(c, t, actor, Scope.Infer, now, cancellationToken: token);
        }
    }
}
