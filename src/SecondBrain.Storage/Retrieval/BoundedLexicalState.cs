using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SecondBrain.Storage.Retrieval;

/// <summary>Generation-bound native phrase contributions, never query results.</summary>
internal sealed class BoundedLexicalState(long budgetBytes = 384L * 1024 * 1024, TimeSpan? fillBudget = null)
{
    private static readonly ConditionalWeakTable<IIndexStore, BoundedLexicalState> States = new();
    private sealed record Catalog(long[] Rowids, string[] Ids, int[] Documents, long[] DocumentRowids, long Bytes);
    private sealed record Posting(int[] Slots, double[] Scores, long Bytes);
    internal sealed record Hit(string Id, long Rowid, int Document, double Score);
    private static readonly IComparer<Hit> Order = Comparer<Hit>.Create((left, right) =>
    {
        var score = left.Score.CompareTo(right.Score);
        return score != 0 ? score : StringComparer.Ordinal.Compare(left.Id, right.Id);
    });
    private readonly Dictionary<string, Posting> postings = new(StringComparer.Ordinal);
    private readonly LinkedList<string> lru = new();
    private Catalog? catalog;
    private long generation = -1;
    private long charged;
    private double[] scores = [];
    private bool[] matched = [];
    private bool[] eligible = [];
    private const long WorkingOverhead = 4096;
    private const long QueryOverhead = 512 * 1024;
    public long AccountedBytes => charged;
    public long PeakBytes { get; private set; }
    public int PrimitiveLoads { get; private set; }
    public int Hits { get; private set; }
    public static BoundedLexicalState For(IIndexStore index) => States.GetValue(index, static _ => new());

    // All access occurs under the existing registry/publication gate. No extra lease,
    // worker, hosted lifecycle, or embedding snapshot is required.
    public async ValueTask<IReadOnlyList<Hit>?> TryRankAsync(DbConnection connection, DbTransaction transaction,
        long currentGeneration, string query, string filter, (string Name, object? Value)[] parameters,
        bool allowFill, TimeSpan remainingFillBudget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != currentGeneration)
        {
            if (catalog is not null) Retire(catalog, catalog.Bytes);
            foreach (var posting in postings.Values) Retire(posting, posting.Bytes);
            catalog = null; postings.Clear(); lru.Clear(); generation = currentGeneration;
        }
        // Charge parsing/pinning/candidate buffers before constructing them, including
        // the maximum 8192-character request with thousands of phrase occurrences.
        if (!Reserve(QueryOverhead)) return null;
        try
        {
            return await RankCoreAsync(connection, transaction, query, filter, parameters, allowFill, remainingFillBudget, cancellationToken);
        }
        finally { charged -= QueryOverhead; }
    }

    private async ValueTask<IReadOnlyList<Hit>?> RankCoreAsync(DbConnection connection, DbTransaction transaction,
        string query, string filter, (string Name, object? Value)[] parameters,
        bool allowFill, TimeSpan remainingFillBudget, CancellationToken cancellationToken)
    {
        var phrases = Primitives(query);
        var ready = catalog is not null && phrases.All(postings.ContainsKey);
        if (!ready)
        {
            if (!allowFill || remainingFillBudget <= TimeSpan.Zero) return null;
            var deadline = fillBudget ?? TimeSpan.FromSeconds(4);
            if (remainingFillBudget < deadline) deadline = remainingFillBudget;
            using var fill = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            fill.CancelAfter(deadline);
            try
            {
                if (catalog is null && !await FillCatalogAsync(connection, transaction, fill.Token)) return null;
                // Workspace cannot be crowded out by a successful sequence of posting fills.
                if (!EnsureWorkspace(catalog!.Rowids.Length, catalog.DocumentRowids.Length)) return null;
                var pinned = new HashSet<string>(StringComparer.Ordinal);
                foreach (var phrase in phrases)
                {
                    fill.Token.ThrowIfCancellationRequested();
                    if (!postings.ContainsKey(phrase) && !await FillPostingAsync(connection, transaction, phrase, pinned, fill.Token)) return null;
                    lru.Remove(phrase); lru.AddLast(phrase); pinned.Add(phrase);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && fill.IsCancellationRequested)
            {
                return null;
            }
        }
        if (!EnsureWorkspace(catalog!.Rowids.Length, catalog.DocumentRowids.Length)) return null;
        Hits++;
        foreach (var phrase in phrases) { lru.Remove(phrase); lru.AddLast(phrase); }
        Array.Clear(scores); Array.Clear(matched); Array.Clear(eligible);
        // Existing SQL filters determine document eligibility, including status, tags,
        // people, paths and property predicates. Missing vectors do not remove chunks.
        await using (var command = Command(connection, transaction, "SELECT d.rowid FROM indexed_documents d WHERE " + filter, parameters))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var document = Array.BinarySearch(catalog.DocumentRowids, reader.GetInt64(0));
                if (document >= 0) eligible[document] = true;
            }
        }
        var workStarted = Stopwatch.GetTimestamp();
        void CheckWork()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(workStarted) > TimeSpan.FromSeconds(4)) throw new TimeoutException("Lexical composition work budget expired.");
        }
        foreach (var phrase in phrases)
        {
            var posting = postings[phrase];
            // Keep duplicate phrases and parsed order: this matches native double rounding.
            for (var i = 0; i < posting.Slots.Length; i++)
            {
                if ((i & 4095) == 0) CheckWork();
                var slot = posting.Slots[i]; scores[slot] += posting.Scores[i]; matched[slot] = true;
            }
        }
        var selected = new SortedSet<Hit>(Order); var groups = new Dictionary<int, List<Hit>>();
        void Remove(Hit candidate)
        {
            selected.Remove(candidate); var group = groups[candidate.Document]; group.Remove(candidate);
            if (group.Count == 0) groups.Remove(candidate.Document);
        }
        for (var slot = 0; slot < catalog.Rowids.Length; slot++)
        {
            if ((slot & 4095) == 0) CheckWork();
            var document = catalog.Documents[slot];
            if (!matched[slot] || !eligible[document]) continue;
            if (selected.Count == 50 && (scores[slot] > selected.Max!.Score || scores[slot] == selected.Max.Score && StringComparer.Ordinal.Compare(catalog.Ids[slot], selected.Max.Id) >= 0)) continue;
            var candidate = new Hit(catalog.Ids[slot], catalog.Rowids[slot], document, scores[slot]);
            if (groups.TryGetValue(document, out var existing) && existing.Count == 3)
            {
                var worst = existing.MaxBy(value => value, Order)!;
                if (Order.Compare(candidate, worst) >= 0) continue;
                Remove(worst);
            }
            if (selected.Count == 50) Remove(selected.Max!);
            selected.Add(candidate);
            if (!groups.TryGetValue(document, out var group)) groups[document] = group = [];
            group.Add(candidate);
        }
        return selected.ToArray();
    }

    private async ValueTask<bool> FillCatalogAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        long chunks, documents, chunkIdBytes;
        // UTF-16 identifier payload is at most twice the SQLite UTF-8 byte count.
        await using (var command = Command(connection, transaction, "SELECT COUNT(*),COALESCE(SUM(length(CAST(id AS BLOB))),0) FROM chunks"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken); chunks = reader.GetInt64(0); chunkIdBytes = reader.GetInt64(1);
        }
        await using (var command = Command(connection, transaction, "SELECT COUNT(*) FROM indexed_documents"))
            documents = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        if (chunks > int.MaxValue || documents > int.MaxValue) return false;
        var bytes = checked(chunks * (8 + 8 + 4 + 40) + chunkIdBytes * 2 + documents * 8 + 4096);
        if (!Reserve(bytes)) return false;
        Catalog? pending = null; var published = false;
        try
        {
            pending = new(new long[(int)chunks], new string[(int)chunks], new int[(int)chunks], new long[(int)documents], bytes);
            await using (var command = Command(connection, transaction, "SELECT rowid FROM indexed_documents ORDER BY rowid"))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                var ordinal = 0;
                while (await reader.ReadAsync(cancellationToken)) pending.DocumentRowids[ordinal++] = reader.GetInt64(0);
                if (ordinal != documents) throw new InvalidDataException("Lexical document catalog changed within a snapshot.");
            }
            await using (var command = Command(connection, transaction, "SELECT c.rowid,c.id,d.rowid FROM chunks c JOIN indexed_documents d ON d.document_id=c.document_id ORDER BY c.rowid"))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                var ordinal = 0;
                while (await reader.ReadAsync(cancellationToken))
                {
                    pending.Rowids[ordinal] = reader.GetInt64(0); pending.Ids[ordinal] = reader.GetString(1);
                    var document = Array.BinarySearch(pending.DocumentRowids, reader.GetInt64(2));
                    if (document < 0) throw new InvalidDataException("Lexical chunk has no committed document.");
                    pending.Documents[ordinal++] = document;
                }
                if (ordinal != chunks) throw new InvalidDataException("Lexical chunk catalog changed within a snapshot.");
            }
            catalog = pending; pending = null; published = true;
            return true;
        }
        finally
        {
            if (!published) charged -= bytes;
        }
    }

    private async ValueTask<bool> FillPostingAsync(DbConnection connection, DbTransaction transaction, string phrase,
        HashSet<string> pinned, CancellationToken cancellationToken)
    {
        await using var count = Command(connection, transaction, "SELECT COUNT(*) FROM chunks_fts WHERE chunks_fts MATCH $phrase", ("$phrase", phrase));
        var size = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        if (size > int.MaxValue) return false;
        var bytes = checked(size * 12 + phrase.Length * 2 + 512);
        var workspaceBytes = scores.Length * 9L + eligible.Length + WorkingOverhead;
        if (bytes > budgetBytes - catalog!.Bytes - workspaceBytes - QueryOverhead) return false;
        while (!Reserve(bytes))
        {
            // The gate prevents a live reader retaining these arrays during eviction.
            var victim = lru.First;
            while (victim is not null && pinned.Contains(victim.Value)) victim = victim.Next;
            if (victim is null) return false;
            var old = postings[victim.Value]; postings.Remove(victim.Value); lru.Remove(victim); Retire(old, old.Bytes);
        }
        Posting? pending = null; var published = false;
        try
        {
            pending = new(new int[(int)size], new double[(int)size], bytes);
            await using var command = Command(connection, transaction, "SELECT rowid,bm25(chunks_fts) FROM chunks_fts WHERE chunks_fts MATCH $phrase ORDER BY rowid", ("$phrase", phrase));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var offset = 0; var ordinal = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                var rowid = reader.GetInt64(0);
                while (ordinal < catalog!.Rowids.Length && catalog.Rowids[ordinal] < rowid)
                {
                    if ((ordinal & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    ordinal++;
                }
                if (ordinal == catalog.Rowids.Length || catalog.Rowids[ordinal] != rowid) throw new InvalidDataException("Native posting is outside the committed lexical catalog.");
                pending.Slots[offset] = ordinal; pending.Scores[offset++] = reader.GetDouble(1);
            }
            if (offset != size) throw new InvalidDataException("Native posting count changed within a snapshot.");
            postings.Add(phrase, pending); pending = null; published = true; PrimitiveLoads++;
            return true;
        }
        finally
        {
            if (!published) charged -= bytes;
        }
    }

    private bool EnsureWorkspace(int chunks, int documents)
    {
        if (scores.Length >= chunks && eligible.Length >= documents) return true;
        var bytes = checked(chunks * 9L + documents + WorkingOverhead);
        while (!Reserve(bytes))
        {
            if (lru.First is not { } victim) return false;
            var old = postings[victim.Value]; postings.Remove(victim.Value); lru.Remove(victim); Retire(old, old.Bytes);
        }
        var published = false;
        try
        {
            var replacementScores = new double[chunks]; var replacementMatched = new bool[chunks]; var replacementEligible = new bool[documents];
            if (scores.Length > 0 || eligible.Length > 0)
            {
                var oldBytes = scores.Length * 9L + eligible.Length + WorkingOverhead;
                Retire(new object[] { scores, matched, eligible }, oldBytes);
            }
            scores = replacementScores; matched = replacementMatched; eligible = replacementEligible;
            published = true; return true;
        }
        finally { if (!published) charged -= bytes; }
    }
    private bool Reserve(long bytes)
    {
        if (bytes < 0 || bytes > budgetBytes - charged) return false;
        charged += bytes; PeakBytes = Math.Max(PeakBytes, charged); return true;
    }
    // Call only once the gated operation has removed every live owner of the data.
    // Unreachable garbage is reclaimed normally; capacity never depends on forced GC.
    private void Retire(object root, long bytes) { _ = root; charged -= bytes; }
    private static string[] Primitives(string query)
    {
        var phrases = new List<string>(); var offset = 0;
        while (offset < query.Length)
        {
            var start = query.IndexOf('"', offset); if (start < 0) break;
            var end = query.IndexOf('"', start + 1); if (end < 0) throw new ArgumentException("Unterminated sanitized phrase.");
            offset = end + 1; if (offset < query.Length && query[offset] == '*') offset++;
            phrases.Add(query[start..offset]);
        }
        return phrases.ToArray();
    }
    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
        => SecondBrain.Storage.Ingestion.SqliteDocumentRepository.Command(connection, transaction, sql, parameters);
}
