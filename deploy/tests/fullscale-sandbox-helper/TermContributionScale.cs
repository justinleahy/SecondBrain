using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Retrieval;
using SecondBrain.Storage.Retrieval;

// Isolated performance/proof prototype. Never loaded by the production daemon.
internal static class TermContributionScale
{
    private sealed record Posting(int[] Slots, double[] Scores, long Bytes);
    private sealed record Ranked(int Slot, string Id, string Document, double Score);
    private static readonly IComparer<Ranked> Order = Comparer<Ranked>.Create((left, right) =>
    {
        var score = left.Score.CompareTo(right.Score);
        return score != 0 ? score : StringComparer.Ordinal.Compare(left.Id, right.Id);
    });
    public static async Task RunAsync(string path)
    {
        if (!Path.GetFullPath(path).Contains("lexical-copy", StringComparison.Ordinal)) throw new ArgumentException("Copied fixture required.");
        const long catalogBudget = 640L * 1024 * 1024, postingBudget = 192L * 1024 * 1024;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var transaction = connection.BeginTransaction(deferred: true);
        SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
        {
            var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            return command;
        }
        var baselineManaged = GC.GetTotalMemory(true);
        int count, maxId, maxDocument;
        await using (var command = Command("SELECT COUNT(*),MAX(length(id)),MAX(length(document_id)) FROM chunks"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            await reader.ReadAsync(); count = checked((int)reader.GetInt64(0)); maxId = reader.GetInt32(1); maxDocument = reader.GetInt32(2);
        }
        // Before allocation: bound payload, array headers, worst-case UTF-16 identifiers
        // and doc-intern dictionary (one document per chunk is the worst case).
        var catalogUpperBound = checked((long)count * (8 + 8 + 8 + 1 + 1 + 4L * maxId + 32 + 4L * maxDocument + 32 + 64) + 4096);
        if (catalogUpperBound > catalogBudget) throw new InvalidDataException($"Catalog conservative bound {catalogUpperBound} exceeds prototype budget.");
        var rowids = new long[count]; var ids = new string[count]; var documents = new string[count]; var available = new bool[count]; var article = new bool[count];
        var internedDocuments = new Dictionary<string, string>(StringComparer.Ordinal);
        var binding = BindingFlags.Static | BindingFlags.NonPublic;
        var filters = new List<(string Name, object? Value)>();
        var availableFilter = (string)typeof(SqliteRetrievalRepository).GetMethod("BuildFilters", binding)!.Invoke(null, [new RetrievalFilters(), filters])!;
        var catalogTimer = Stopwatch.StartNew();
        await using (var command = Command("SELECT c.rowid,c.id,c.document_id,d.type,CASE WHEN " + availableFilter + " THEN 1 ELSE 0 END FROM chunks c JOIN indexed_documents d ON d.document_id=c.document_id ORDER BY c.rowid"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            var slot = 0;
            while (await reader.ReadAsync())
            {
                rowids[slot] = reader.GetInt64(0); ids[slot] = reader.GetString(1);
                var document = reader.GetString(2);
                if (!internedDocuments.TryGetValue(document, out var canonical)) internedDocuments[document] = canonical = document;
                documents[slot] = canonical; article[slot] = reader.GetString(3) == "article"; available[slot] = reader.GetInt32(4) == 1; slot++;
            }
            if (slot != count) throw new InvalidDataException("Missing catalog document.");
        }
        var catalogMilliseconds = catalogTimer.Elapsed.TotalMilliseconds;
        var catalogManagedBytes = GC.GetTotalMemory(true) - baselineManaged;
        var entries = new Dictionary<string, Posting>(StringComparer.Ordinal); var lru = new LinkedList<string>();
        long postingBytes = 0, peakPostingBytes = 0; var evictions = 0; var loads = 0; var fallbackCount = 0;
        var sanitize = typeof(SqliteRetrievalRepository).GetMethod("SanitizeFts", binding)!;
        var queries = File.ReadAllLines("eval/questions.jsonl").Where(line => line.Length > 0).Select(line => JsonDocument.Parse(line)).Select(document => (Id: document.RootElement.GetProperty("id").GetString()!, Text: document.RootElement.GetProperty("query").GetString()!)).ToArray();
        var reports = new List<object>();
        foreach (var query in queries)
        {
            var fts = (string)sanitize.Invoke(null, [query.Text])!;
            var phrases = Regex.Matches(fts, "\"[^\"]+\"\\*?").Select(match => match.Value).ToArray();
            var pinned = new HashSet<string>(StringComparer.Ordinal); var fillTimer = Stopwatch.StartNew(); var fallback = false;
            foreach (var phrase in phrases)
            {
                if (entries.ContainsKey(phrase)) { lru.Remove(phrase); lru.AddLast(phrase); pinned.Add(phrase); continue; }
                await using var countCommand = Command("SELECT COUNT(*) FROM chunks_fts WHERE chunks_fts MATCH $phrase", ("$phrase", phrase));
                var postings = checked((int)(long)(await countCommand.ExecuteScalarAsync())!);
                var bytes = checked(postings * 12L + 256 + phrase.Length * 2);
                while (postingBytes + bytes > postingBudget)
                {
                    var victim = lru.First; while (victim is not null && pinned.Contains(victim.Value)) victim = victim.Next;
                    if (victim is null) { fallback = true; fallbackCount++; break; }
                    postingBytes -= entries[victim.Value].Bytes; entries.Remove(victim.Value); lru.Remove(victim); evictions++;
                }
                if (fallback) break;
                postingBytes += bytes; peakPostingBytes = Math.Max(peakPostingBytes, postingBytes);
                var slots = new int[postings]; var scores = new double[postings];
                await using var load = Command("SELECT rowid,bm25(chunks_fts) FROM chunks_fts WHERE chunks_fts MATCH $phrase ORDER BY rowid", ("$phrase", phrase));
                await using var reader = await load.ExecuteReaderAsync(); var ordinal = 0; var offset = 0;
                while (await reader.ReadAsync())
                {
                    var rowid = reader.GetInt64(0); while (ordinal < count && rowids[ordinal] < rowid) ordinal++;
                    if (ordinal == count || rowids[ordinal] != rowid) throw new InvalidDataException("Posting outside catalog.");
                    slots[offset] = ordinal; scores[offset++] = reader.GetDouble(1);
                }
                if (offset != postings) throw new InvalidDataException("Posting snapshot changed.");
                entries.Add(phrase, new(slots, scores, bytes)); lru.AddLast(phrase); pinned.Add(phrase); loads++;
            }
            var fillMilliseconds = fillTimer.Elapsed.TotalMilliseconds;
            if (fallback) { reports.Add(new { query_id = query.Id, native_fallback_required = true, fill_milliseconds = fillMilliseconds }); continue; }
            var composed = new double[count]; var matched = new bool[count]; var composition = new List<double>();
            for (var repeat = 0; repeat < 3; repeat++)
            {
                fillTimer.Restart(); Array.Clear(composed); Array.Clear(matched);
                foreach (var phrase in phrases)
                {
                    var posting = entries[phrase];
                    for (var i = 0; i < posting.Slots.Length; i++) { var slot = posting.Slots[i]; composed[slot] += posting.Scores[i]; matched[slot] = true; }
                }
                composition.Add(fillTimer.Elapsed.TotalMilliseconds);
            }
            // Independent whole-query native scores; no cached result set is used as oracle.
            await using (var oracle = Command("SELECT rowid,bm25(chunks_fts) FROM chunks_fts WHERE chunks_fts MATCH $query ORDER BY rowid", ("$query", fts)))
            await using (var reader = await oracle.ExecuteReaderAsync())
            {
                var ordinal = 0; var nativeMatches = 0;
                while (await reader.ReadAsync())
                {
                    var rowid = reader.GetInt64(0); while (ordinal < count && rowids[ordinal] < rowid) ordinal++;
                    if (ordinal == count || rowids[ordinal] != rowid || !matched[ordinal] || BitConverter.DoubleToInt64Bits(composed[ordinal]) != BitConverter.DoubleToInt64Bits(reader.GetDouble(1))) throw new InvalidDataException("Million-row score contribution mismatch.");
                    nativeMatches++;
                }
                if (nativeMatches != matched.Count(value => value)) throw new InvalidDataException("Million-row composed match set differs.");
            }
            var rankingReports = new List<object>();
            foreach (var filtered in new[] { false, true })
            {
                var measured = new List<double>(); var selected = new SortedSet<Ranked>(Order);
                for (var repeat = 0; repeat < 3; repeat++)
                {
                    fillTimer.Restart(); selected.Clear(); var groups = new Dictionary<string, List<Ranked>>(StringComparer.Ordinal);
                    void Remove(Ranked candidate) { selected.Remove(candidate); var group = groups[candidate.Document]; group.Remove(candidate); if (group.Count == 0) groups.Remove(candidate.Document); }
                    for (var slot = 0; slot < count; slot++)
                    {
                        if (!matched[slot] || !available[slot] || filtered && !article[slot]) continue;
                        if (selected.Count == 50 && (composed[slot] > selected.Max!.Score || composed[slot] == selected.Max.Score && StringComparer.Ordinal.Compare(ids[slot], selected.Max.Id) >= 0)) continue;
                        var candidate = new Ranked(slot, ids[slot], documents[slot], composed[slot]);
                        if (groups.TryGetValue(candidate.Document, out var existing) && existing.Count == 3)
                        {
                            var worst = existing.MaxBy(value => value, Order)!; if (Order.Compare(candidate, worst) >= 0) continue; Remove(worst);
                        }
                        if (selected.Count == 50) Remove(selected.Max!); selected.Add(candidate);
                        if (!groups.TryGetValue(candidate.Document, out var group)) groups[candidate.Document] = group = []; group.Add(candidate);
                    }
                    measured.Add(fillTimer.Elapsed.TotalMilliseconds);
                }
                var prefix = 256; var nativeTop = new List<string>();
                while (true)
                {
                    nativeTop.Clear(); var groups = new Dictionary<string, int>(); var scanned = 0;
                    await using var rank = Command("SELECT c.id,c.document_id FROM chunks_fts JOIN chunks c ON c.rowid=chunks_fts.rowid JOIN indexed_documents d ON d.document_id=c.document_id WHERE chunks_fts MATCH $query AND " + availableFilter + (filtered ? " AND d.type='article'" : "") + " ORDER BY bm25(chunks_fts),c.id LIMIT $prefix", ("$query", fts), ("$prefix", prefix));
                    await using var reader = await rank.ExecuteReaderAsync();
                    while (await reader.ReadAsync()) { scanned++; var document = reader.GetString(1); groups.TryGetValue(document, out var n); if (n == 3) continue; groups[document] = n + 1; nativeTop.Add(reader.GetString(0)); if (nativeTop.Count == 50) break; }
                    if (nativeTop.Count == 50 || scanned < prefix) break; prefix = checked(prefix * 4);
                }
                if (!nativeTop.SequenceEqual(selected.Select(value => value.Id))) throw new InvalidDataException("Million-row native diverse top50 differs.");
                rankingReports.Add(new { filtered, warm_ranking_milliseconds = measured, exact_native_diverse_top50_equal = true });
            }
            reports.Add(new { query_id = query.Id, primitive_count = phrases.Length, fill_milliseconds = fillMilliseconds, composition_milliseconds = composition, exact_native_all_scores_and_match_set_equal = true, ranking = rankingReports });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { workload = "isolated dense primitive contribution prototype against copied million-chunk corpus; no production qualification", chunks = count, catalog_milliseconds = catalogMilliseconds, catalog_budget_bytes = catalogBudget, catalog_conservative_upper_bound_bytes = catalogUpperBound, catalog_retained_managed_bytes = catalogManagedBytes, posting_budget_bytes = postingBudget, peak_accounted_posting_bytes = peakPostingBytes, working_score_and_match_bytes = count * 9L + 512, maximum_combined_managed_derived_upper_bound_bytes = catalogUpperBound + postingBudget + count * 9L + 8192, loads, evictions, fallback_count = fallbackCount, reports }));
    }
}
