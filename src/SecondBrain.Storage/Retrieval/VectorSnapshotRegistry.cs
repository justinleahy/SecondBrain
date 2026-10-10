using System.Buffers.Binary;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using SecondBrain.Core.Retrieval;

namespace SecondBrain.Storage.Retrieval;

/// <summary>
/// Pairs committed SQL snapshots with dense vector generations. All vector-affecting writers
/// and vector readers hold the same gate. The cache applies committed deltas on first read,
/// so rollback leaves it unchanged and process restart rebuilds solely from live SQLite rows.
/// </summary>
public sealed class VectorSnapshotRegistry
{
    private static readonly ConditionalWeakTable<IIndexStore, VectorSnapshotRegistry> Registries = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private long generation;
    private long loadedGeneration = -1;
    private long corpusHashGeneration = -1;
    private string? corpusHash;
    private readonly HashSet<string> dirtyDocuments = new(StringComparer.Ordinal);
    private bool reloadAll = true;
    // Read only while holding the registry gate; fences derived lexical state too.
    internal long Generation => generation;
    internal bool IsCurrent(string spaceId, int dimensions) => slots is not null &&
        loadedSpace == spaceId && loadedDimensions == dimensions && loadedGeneration == generation;
    public long VectorRowsRead { get; private set; }
    public long VectorReadStatements { get; private set; }
    private string? loadedSpace;
    private int loadedDimensions;
    private VectorSlotMap? slots;
    private Dictionary<string, List<string>> documentChunks = new(StringComparer.Ordinal);
    private HashSet<string> pendingDocuments = new(StringComparer.Ordinal);

    // Access while holding the registry gate after acquiring the matching vector snapshot.
    public IReadOnlyList<string> ChunkIds(string documentId) => documentChunks.TryGetValue(documentId, out var ids) ? ids : [];
    public bool HasMissingVectors(string documentId) => pendingDocuments.Contains(documentId);

    public static VectorSnapshotRegistry For(IIndexStore index) => Registries.GetValue(index, static _ => new());

    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    /// <summary>Call with the gate held, immediately after a successful vector-affecting SQL commit.</summary>
    public void Committed(string? documentId = null)
    {
        generation++;
        if (documentId is null) reloadAll = true;
        else dirtyDocuments.Add(documentId);
    }

    /// <summary>Call with the gate held and the matching committed SQL snapshot open.</summary>
    public async ValueTask<string> CorpusGenerationAsync(DbConnection connection, DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        if (corpusHashGeneration == generation && corpusHash is not null) return corpusHash;
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT COALESCE(group_concat(document_id || ':' || generation_json,'|'),'') FROM (SELECT document_id,generation_json FROM indexed_document_generations ORDER BY document_id)";
        var persisted = (string)(await read.ExecuteScalarAsync(cancellationToken))!;
        corpusHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(persisted)));
        corpusHashGeneration = generation;
        return corpusHash;
    }

    /// <summary>Call with the gate held and retain it until the returned lease is disposed.</summary>
    public async ValueTask<VectorSlotMap.Snapshot> AcquireAsync(DbConnection connection, DbTransaction transaction,
        string spaceId, int dimensions, CancellationToken cancellationToken = default)
    {
        if (slots is null || loadedSpace != spaceId || loadedDimensions != dimensions)
        {
            slots = new VectorSlotMap(dimensions);
            loadedGeneration = -1;
            loadedSpace = spaceId;
            loadedDimensions = dimensions;
            reloadAll = true;
        }
        if (loadedGeneration != generation)
        {
            var updates = new Dictionary<string, float[]>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var groups = new Dictionary<string, List<(int Ordinal, string Id)>>(StringComparer.Ordinal);
            var pending = new HashSet<string>(StringComparer.Ordinal);
            // Bulk publication (notably rebuild) dirties the whole corpus. Thousands of
            // individual statements consume the SQLite lease even when the vectors did
            // not change. Read that snapshot in rowid batches, reusing the live array.
            // Keep small publications incremental so an ordinary edit stays proportional
            // to its document rather than to the corpus.
            var bulkRefresh = dirtyDocuments.Count >= 256 &&
                dirtyDocuments.Count >= (documentChunks.Count + pendingDocuments.Count + 3) / 4;
            var changed = reloadAll || bulkRefresh ? null : dirtyDocuments.ToArray();
            var previousIds = changed is null ? null : changed.SelectMany(ChunkIds).ToArray();
            string[] removals;
            VectorSlotMap.Snapshot? previous = slots.AcquireSnapshot();
            try
            {
                long? after = null;
                var changedIndex = 0;
                while (true)
                {
                    if (changed is not null && changedIndex == changed.Length) break;
                    var readCount = 0;
                    await using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "SELECT c.rowid,c.id,c.document_id,c.ordinal,v.vector FROM chunks c LEFT JOIN chunk_vectors v ON v.chunk_rowid=c.rowid AND v.space_id=$space" +
                            (changed is null ? (after is null ? "" : " WHERE c.rowid>$after") : " WHERE c.document_id=$document" + (after is null ? "" : " AND c.rowid>$after")) + " ORDER BY c.rowid LIMIT 16384";
                        if (changed is not null) { var document = command.CreateParameter(); document.ParameterName = "$document"; document.Value = changed[changedIndex]; command.Parameters.Add(document); }
                        var parameter = command.CreateParameter(); parameter.ParameterName = "$space"; parameter.Value = spaceId; command.Parameters.Add(parameter);
                        if (after is not null) { var cursor = command.CreateParameter(); cursor.ParameterName = "$after"; cursor.Value = after.Value; command.Parameters.Add(cursor); }
                        VectorReadStatements++;
                        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            readCount++; after = reader.GetInt64(0);
                            var id = reader.GetString(1); var documentId = reader.GetString(2);
                            if (reader.IsDBNull(4)) { pending.Add(documentId); continue; }
                            if (!groups.TryGetValue(documentId, out var ids)) groups[documentId] = ids = [];
                            ids.Add((reader.GetInt32(3), id));
                            VectorRowsRead++;
                            var bytes = (byte[])reader.GetValue(4);
                            if (bytes.Length != checked(dimensions * sizeof(float))) throw new InvalidDataException("Stored vector dimensions disagree with the space.");
                            var vector = new float[dimensions];
                            if (BitConverter.IsLittleEndian) Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
                            else for (var i = 0; i < dimensions; i++) vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)));
                            seen.Add(id);
                            if (!previous.TryGetVector(id, out var old) || !old.Span.SequenceEqual(vector)) updates.Add(id, vector);
                        }
                    }
                    // Close each bounded SQLite statement before normalization/copying work.
                    if (updates.Count >= 16384 || changed is null && updates.Count != 0)
                    {
                        previous.Dispose(); previous = null;
                        using (var batch = slots.Prepare(updates, [])) batch.Commit();
                        updates.Clear(); previous = slots.AcquireSnapshot();
                    }
                    if (readCount < 16384)
                    {
                        if (changed is null) break;
                        changedIndex++; after = null;
                    }
                }
                removals = (previousIds ?? previous.Entries.Select(pair => pair.Key)).Where(id => !seen.Contains(id)).ToArray();
            }
            catch
            {
                // A bounded load may already have committed earlier batches. Discard the
                // incomplete cache so retry cannot retain slots absent from its document map.
                slots = null; loadedGeneration = -1; reloadAll = true;
                documentChunks.Clear(); pendingDocuments.Clear();
                throw;
            }
            finally { previous?.Dispose(); }
            using var mutation = slots.Prepare(updates, removals);
            mutation.Commit();
            if (changed is null) { documentChunks.Clear(); pendingDocuments.Clear(); }
            else foreach (var id in changed) { documentChunks.Remove(id); pendingDocuments.Remove(id); }
            foreach (var (id, chunks) in groups) documentChunks[id] = chunks.OrderBy(item => item.Ordinal).Select(item => item.Id).ToList();
            pendingDocuments.UnionWith(pending);
            dirtyDocuments.Clear(); reloadAll = false;
            loadedGeneration = generation;
        }
        return slots.AcquireSnapshot();
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) gate.Release(); }
    }
}
