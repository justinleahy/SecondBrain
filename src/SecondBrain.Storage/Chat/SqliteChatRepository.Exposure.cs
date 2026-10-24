using System.Data.Common;
using System.Text;
using System.Text.Json;
using Dapper;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Storage.Chat;

public sealed partial class SqliteChatRepository
{
    public ValueTask<IReadOnlyList<ChatSource>> ExposeAsync(string turnId, IReadOnlyList<ChatSourceRequest> sources,
        AuthenticatedCredential actor, CancellationToken cancellationToken = default) => state.QueueWriteAsync<IReadOnlyList<ChatSource>>(async (connection, transaction, token) =>
    {
        if (sources.Count > 128) throw new ChatLimitException("Too many source passages in one request.");
        var turn = await RunningAsync(connection, transaction, turnId, actor, token);
        var policy = Policy(turn);
        var existing = (await ReadSourcesAsync(connection, transaction, turnId, token)).ToList();
        var exposedDocuments = (await connection.QueryAsync<string>(Sql("SELECT DISTINCT document_id FROM chat_sources WHERE turn_id=@turnId", new { turnId }, transaction, token))).ToHashSet(StringComparer.Ordinal);
        var nextNumber = await connection.ExecuteScalarAsync<int>(Sql("SELECT COALESCE(MAX(number),0)+1 FROM chat_sources WHERE turn_id=@turnId", new { turnId }, transaction, token));
        var bytes = await connection.ExecuteScalarAsync<long>(Sql("SELECT tool_result_bytes+(SELECT COALESCE(SUM(byte_count),0) FROM chat_sources WHERE turn_id=@turnId) FROM chat_turn_details WHERE turn_id=@turnId", new { turnId }, transaction, token));
        List<ChatSource> selected = [];
        foreach (var request in sources.Distinct())
        {
            var projection = await connection.QuerySingleOrDefaultAsync<ProjectionRow>(Sql("""
                SELECT d.title Title,p.generation_json Generation,b.bytes Bytes
                FROM document_projections p JOIN documents d ON d.id=p.document_id JOIN payloads b ON b.id=p.payload_id
                WHERE d.id=@id AND d.revision=@revision AND p.revision=d.revision
                  AND d.status NOT IN ('deleted','purged','missing','failed') AND b.redacted_at IS NULL
                """, new { id = request.DocumentId, revision = request.Revision }, transaction, token))
                ?? throw new ChatConflictException("A retrieved source changed or is no longer available.");
            var previous = existing.FirstOrDefault(source => source.DocumentId == request.DocumentId && source.Revision == request.Revision &&
                source.Generation == projection.Generation && source.ChunkId == request.ChunkId);
            if (previous is not null) { selected.Add(previous); continue; }
            var text = JsonSerializer.Deserialize<TextProjection>(projection.Bytes, Json) ?? throw new InvalidDataException("Invalid document projection.");
            var chunk = text.Chunks.SingleOrDefault(chunk => chunk.Id == request.ChunkId)
                ?? throw new ChatConflictException("The retrieved passage is no longer current.");
            var source = new ChatSource(nextNumber, request.DocumentId, request.Revision, projection.Generation,
                chunk.Id, chunk.Ordinal, chunk.Page, projection.Title, chunk.Text, Hash(Encoding.UTF8.GetBytes(chunk.Text)));
            var byteCount = JsonSerializer.SerializeToUtf8Bytes(source, Json).Length;
            if (bytes + byteCount > policy.MaxReadBytes || !exposedDocuments.Contains(source.DocumentId) && exposedDocuments.Count >= policy.MaxReadDocuments)
                throw new ChatLimitException("This turn reached its source exposure limit.");
            var payload = await PayloadAsync(connection, transaction, source, [source.DocumentId], "chat-source", turnId, token);
            await connection.ExecuteAsync(Sql("""
                INSERT INTO chat_sources(turn_id,number,document_id,revision,generation_json,chunk_id,byte_count,payload_id)
                VALUES(@turnId,@number,@documentId,@revision,@generation,@chunkId,@byteCount,@payload)
                """, new { turnId, number = source.Number, documentId = source.DocumentId, revision = source.Revision,
                generation = source.Generation, chunkId = source.ChunkId, byteCount, payload }, transaction, token));
            bytes += byteCount;
            nextNumber++;
            exposedDocuments.Add(source.DocumentId);
            existing.Add(source);
            selected.Add(source);
        }
        if (sources.Count > 0) await connection.ExecuteAsync(Sql("UPDATE turns SET retrieved_content=1 WHERE id=@turnId", new { turnId }, transaction, token));
        return selected;
    }, cancellationToken);

    public ValueTask<IReadOnlyList<ChatSource>> SourcesAsync(string turnId, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => ReadAsync<IReadOnlyList<ChatSource>>(async (connection, transaction, token) =>
    {
        await AuthorizeAsync(connection, transaction, actor, false, token);
        await OwnTurnAsync(connection, transaction, turnId, actor, token);
        return await ReadSourcesAsync(connection, transaction, turnId, token);
    }, cancellationToken);

    public ValueTask CountToolAsync(string turnId, int metadataRecords, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => new(state.QueueWriteAsync(async (connection, transaction, token) =>
    {
        if (metadataRecords < 0) throw new ArgumentOutOfRangeException(nameof(metadataRecords));
        var turn = await RunningAsync(connection, transaction, turnId, actor, token);
        var policy = Policy(turn);
        var affected = await connection.ExecuteAsync(Sql("""
            UPDATE chat_turn_details SET tool_calls=tool_calls+1,metadata_records=metadata_records+@metadataRecords
            WHERE turn_id=@turnId AND tool_calls<@calls AND metadata_records+@metadataRecords<=@records
            """, new { turnId, metadataRecords, calls = policy.MaxToolCalls, records = policy.MaxMetadataRecords }, transaction, token));
        if (affected != 1) throw new ChatLimitException("This turn reached its tool or metadata limit.");
        return 0;
    }, cancellationToken).AsTask());

    private static async Task<IReadOnlyList<ChatSource>> ReadSourcesAsync(DbConnection connection, DbTransaction transaction, string turnId, CancellationToken token)
    {
        var rows = await connection.QueryAsync<byte[]>(Sql("""
            SELECT p.bytes FROM chat_sources s JOIN payloads p ON p.id=s.payload_id
            WHERE s.turn_id=@turnId AND p.redacted_at IS NULL ORDER BY s.number
            """, new { turnId }, transaction, token));
        return rows.Select(bytes => JsonSerializer.Deserialize<ChatSource>(bytes, Json) ?? throw new InvalidDataException("Invalid chat source.")).ToArray();
    }

    private sealed class ProjectionRow
    {
        public string Title { get; set; } = "";
        public string Generation { get; set; } = "";
        public byte[] Bytes { get; set; } = [];
    }
}
