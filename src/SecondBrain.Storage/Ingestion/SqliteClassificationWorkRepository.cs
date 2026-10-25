using System.Data.Common;
using System.Text.Json;
using Dapper;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Storage.Durability;

namespace SecondBrain.Storage.Ingestion;

public sealed class SqliteClassificationWorkRepository(IStateStore state) : IClassificationWorkRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<ClassificationContext?> ReadAsync(string documentId, long revision, CancellationToken token = default)
    {
        await using var read = await state.OpenReadConnectionAsync(token);
        return await ReadContextAsync(read.Connection, null, documentId, revision, token);
    }
    internal static async Task<ClassificationContext?> ReadContextAsync(DbConnection c, DbTransaction? t, string documentId, long revision, CancellationToken token)
    {
        var row = await c.QuerySingleOrDefaultAsync<ContextRow>(new CommandDefinition($"""
            SELECT d.type Type,d.type_origin Origin,d.content_hash Hash,f.generation_json Generation,s.config_json Config,
              (SELECT path FROM occurrences WHERE document_id=d.id ORDER BY path LIMIT 1) Path,
              (SELECT value_json FROM overrides WHERE document_id=d.id AND kind='type' AND key='') Override
            FROM documents d JOIN sources s ON s.id=d.source_id JOIN publication_fences f ON f.document_id=d.id
            WHERE d.id=@documentId AND d.revision=@revision AND d.status NOT IN ({DocumentStatusSql.Unavailable})
            """, new { documentId, revision }, t, cancellationToken: token));
        if (row is null) return null;
        var source = JsonSerializer.Deserialize<FolderSourceConfiguration>(row.Config, Json);
        var mapped = source?.TypeMap is null || row.Path is null ? null : source.TypeMap.FirstOrDefault(pair =>
            System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pair.Key, row.Path, false)).Value;
        var overridden = row.Override is null ? null : JsonSerializer.Deserialize<string>(row.Override, Json);
        var stamp = ContentTypeSelection.Hash(JsonSerializer.Serialize(new { row.Type, row.Origin, row.Hash, row.Generation, row.Config, row.Path, row.Override }));
        return new(documentId, revision, row.Generation, row.Hash, stamp, row.Type, row.Origin, overridden, mapped, source?.DefaultType);
    }
    public async Task<ContentClassification?> CachedAsync(ClassificationContext work, string fingerprint, CancellationToken token = default)
    {
        await using var read = await state.OpenReadConnectionAsync(token);
        var json = await read.Connection.ExecuteScalarAsync<string>(new CommandDefinition("""
            SELECT json_extract(payload_json,'$.classification') FROM jobs
            WHERE document_id=@DocumentId AND revision=@Revision AND generation_json=@GenerationJson AND kind='extract'
              AND json_extract(payload_json,'$.classification.fingerprint')=@fingerprint ORDER BY id LIMIT 1
            """, new { work.DocumentId, work.Revision, work.GenerationJson, fingerprint }, cancellationToken: token));
        return json is null ? null : JsonSerializer.Deserialize<ContentClassification>(json, Json);
    }
    public Task<bool> SaveAsync(ClassificationContext work, ContentClassification result, CancellationToken token = default) =>
        state.QueueWriteAsync(async (c, t, ct) =>
        {
            var current = await ReadContextAsync(c, t, work.DocumentId, work.Revision, ct);
            if (current?.Stamp != work.Stamp || current.GenerationJson != work.GenerationJson) return false;
            return await c.ExecuteAsync(new CommandDefinition("""
                UPDATE jobs SET payload_json=json_set(payload_json,'$.classification',json(@json))
                WHERE document_id=@DocumentId AND revision=@Revision AND generation_json=@GenerationJson AND kind='extract' AND status IN ('queued','running')
                """, new { work.DocumentId, work.Revision, work.GenerationJson, json = JsonSerializer.Serialize(result, Json) }, t, cancellationToken: ct)) != 0;
        }, token).AsTask();
    private sealed class ContextRow
    {
        public string Type { get; set; } = "";
        public string Origin { get; set; } = "";
        public string Hash { get; set; } = "";
        public string Generation { get; set; } = "";
        public string Config { get; set; } = "{}";
        public string? Path { get; set; }
        public string? Override { get; set; }
    }
}
