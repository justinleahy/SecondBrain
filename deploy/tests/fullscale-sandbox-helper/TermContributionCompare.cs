using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SecondBrain.Storage.Retrieval;

// Diagnostic prototype only. No production retrieval code depends on this class.
internal static class TermContributionCompare
{
    private sealed record Posting(long[] Rowids, double[] Scores, long Bytes);
    private sealed class PrimitiveCache(long budget)
    {
        private readonly Dictionary<string, Posting> entries = new(StringComparer.Ordinal);
        private readonly LinkedList<string> lru = new();
        private long generation = -1;
        public long Bytes { get; private set; }
        public long PeakBytes { get; private set; }
        public int Loads { get; private set; }
        public int Hits { get; private set; }
        public int Evictions { get; private set; }
        public int Fallbacks { get; private set; }
        public int GenerationInvalidations { get; private set; }
        public async Task<Dictionary<long, double>?> Compose(SqliteConnection connection, string[] phrases, long currentGeneration)
        {
            if (generation != currentGeneration) { if (generation != -1) GenerationInvalidations++; entries.Clear(); lru.Clear(); Bytes = 0; generation = currentGeneration; }
            // Pin all primitives for this composition. Eviction cannot retain an unaccounted
            // array through a query-local reference after removing it from the cache.
            var pinned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var phrase in phrases)
            {
                if (entries.ContainsKey(phrase)) { Hits++; lru.Remove(phrase); lru.AddLast(phrase); pinned.Add(phrase); continue; }
                await using var count = connection.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM chunks_fts WHERE chunks_fts MATCH $phrase";
                count.Parameters.AddWithValue("$phrase", phrase); var size = (long)(await count.ExecuteScalarAsync())!;
                // Conservative managed-layout allowance includes two arrays, dictionary/LRU
                // entries and the UTF-16 key; the double/Int64 payload is exactly 16*n.
                var bytes = checked(size * 16 + 256 + phrase.Length * 2);
                while (Bytes + bytes > budget)
                {
                    var victim = lru.First;
                    while (victim is not null && pinned.Contains(victim.Value)) victim = victim.Next;
                    if (victim is null) { Fallbacks++; return null; }
                    Bytes -= entries[victim.Value].Bytes; entries.Remove(victim.Value); lru.Remove(victim); Evictions++;
                }
                if (size > int.MaxValue) { Fallbacks++; return null; }
                Bytes += bytes; PeakBytes = Math.Max(PeakBytes, Bytes);
                var rowids = new long[(int)size]; var scores = new double[(int)size];
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT rowid,bm25(chunks_fts) FROM chunks_fts WHERE chunks_fts MATCH $phrase ORDER BY rowid";
                command.Parameters.AddWithValue("$phrase", phrase); await using var reader = await command.ExecuteReaderAsync(); var offset = 0;
                while (await reader.ReadAsync()) { rowids[offset] = reader.GetInt64(0); scores[offset++] = reader.GetDouble(1); }
                if (offset != size) throw new InvalidDataException("Snapshot posting count changed.");
                entries.Add(phrase, new(rowids, scores, bytes)); lru.AddLast(phrase); pinned.Add(phrase); Loads++;
            }
            var composed = new Dictionary<long, double>();
            // This exact order matters: native bm25 accumulates phrase contributions in
            // parsed phrase order. Keep duplicate phrase occurrences rather than deduplicating.
            foreach (var phrase in phrases)
            {
                var posting = entries[phrase];
                for (var i = 0; i < posting.Rowids.Length; i++)
                {
                    composed.TryGetValue(posting.Rowids[i], out var previous);
                    composed[posting.Rowids[i]] = previous + posting.Scores[i];
                }
            }
            return composed;
        }
    }

    public static async Task RunAsync()
    {
        var timer = Stopwatch.StartNew(); var comparisons = 0; var scoreRows = 0L;
        var reports = new List<object>();
        foreach (var seed in new[] { 12, 37, 103 })
        {
            await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            await using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "CREATE TABLE chunks_fts_source(rowid INTEGER PRIMARY KEY,text,context,title); CREATE VIRTUAL TABLE chunks_fts USING fts5(text,context,title,content='chunks_fts_source',content_rowid='rowid',tokenize='unicode61 remove_diacritics 2'); CREATE TABLE chunks(rowid INTEGER PRIMARY KEY,id,document_id,type,status);";
                await schema.ExecuteNonQueryAsync();
            }
            var random = new Random(seed);
            var words = new[] { "red", "giant", "red giant", "café", "cafe\u0301", "telescope", "telescopes", "telescopic", "astronomy", "astro\u200Bnomy", "東京", "orbit", "OR", "alpha_beta", "alpha", "beta", "rare", "🪐", "résumé", "resume" };
            await using (var transaction = connection.BeginTransaction())
            {
                for (var row = 1; row <= 4096; row++)
                {
                    var text = string.Join(' ', Enumerable.Range(0, random.Next(3, 70)).Select(_ => words[random.Next(words.Length)]));
                    var context = string.Join(' ', Enumerable.Range(0, random.Next(0, 12)).Select(_ => words[random.Next(words.Length)]));
                    var title = string.Join(' ', Enumerable.Range(0, random.Next(1, 6)).Select(_ => words[random.Next(words.Length)]));
                    await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO chunks VALUES($row,$id,$doc,$type,$status); INSERT INTO chunks_fts_source VALUES($row,$text,$context,$title); INSERT INTO chunks_fts(rowid,text,context,title) VALUES($row,$text,$context,$title)";
                    foreach (var item in new (string, object)[] { ("$row", row * 3L), ("$id", $"chunk-{4097-row:00000}"), ("$doc", $"doc-{row/7:00000}"), ("$type", row % 4 == 0 ? "article" : "note"), ("$status", row % 19 == 0 ? "removed_from_source" : row % 11 == 0 ? "indexed_partial" : "indexed"), ("$text", text.Normalize()), ("$context", context.Normalize()), ("$title", title.Normalize()) }) insert.Parameters.AddWithValue(item.Item1, item.Item2);
                    await insert.ExecuteNonQueryAsync();
                }
                transaction.Commit();
            }
            var cache = new PrimitiveCache(2 * 1024 * 1024); var constrained = new PrimitiveCache(1024); var evicting = new PrimitiveCache(128 * 1024); var generation = 1L;
            var queries = new[] { "red giant", "\"red giant\" telescope", "cafe café cafe\u0301", "telesc* telescope telesc*", "red red red", "\"red giant\" \"red giant\"", "\"alpha beta\" alpha_beta", "東京 orbit", "\"OR red\" OR", "résumé resume", "rare absentnomatch", "absentnomatch", "red* giant cafe telesc* 東京 \"alpha beta\"" };
            var sanitize = typeof(SqliteRetrievalRepository).GetMethod("SanitizeFts", BindingFlags.NonPublic | BindingFlags.Static)!;
            async Task Check(string raw, bool filtered, PrimitiveCache tested)
            {
                var query = (string)sanitize.Invoke(null, [raw])!;
                var phrases = Regex.Matches(query, "\"[^\"]+\"\\*?").Select(match => match.Value).ToArray();
                var composed = await tested.Compose(connection, phrases, generation);
                await using var oracleCommand = connection.CreateCommand();
                oracleCommand.CommandText = "SELECT c.rowid,c.id,c.document_id,bm25(chunks_fts) FROM chunks_fts JOIN chunks c ON c.rowid=chunks_fts.rowid WHERE chunks_fts MATCH $query AND c.status!='removed_from_source'" + (filtered ? " AND c.type='article'" : "") + " ORDER BY bm25(chunks_fts),c.id";
                oracleCommand.Parameters.AddWithValue("$query", query); await using var oracleReader = await oracleCommand.ExecuteReaderAsync();
                var native = new List<(long Row, string Id, string Document, double Score)>();
                while (await oracleReader.ReadAsync()) native.Add((oracleReader.GetInt64(0), oracleReader.GetString(1), oracleReader.GetString(2), oracleReader.GetDouble(3)));
                await oracleReader.DisposeAsync();
                if (composed is not null)
                {
                    foreach (var row in native)
                        if (!composed.TryGetValue(row.Row, out var score) || BitConverter.DoubleToInt64Bits(score) != BitConverter.DoubleToInt64Bits(row.Score))
                            throw new InvalidDataException($"Exact contribution failure seed={seed}, query={raw}, row={row.Row}, native={row.Score:R}, composed={score:R}.");
                    // Build the result set independently from the oracle MATCH rows. This also
                    // catches false-positive composed matches or accidentally omitted filters.
                    var derivedRows = new List<(long Row, string Id, string Document, double Score)>();
                    await using var eligible = connection.CreateCommand();
                    eligible.CommandText = "SELECT rowid,id,document_id FROM chunks WHERE status!='removed_from_source'" + (filtered ? " AND type='article'" : "");
                    await using var eligibleReader = await eligible.ExecuteReaderAsync();
                    while (await eligibleReader.ReadAsync())
                        if (composed.TryGetValue(eligibleReader.GetInt64(0), out var score))
                            derivedRows.Add((eligibleReader.GetInt64(0), eligibleReader.GetString(1), eligibleReader.GetString(2), score));
                    var derived = derivedRows.OrderBy(row => row.Score).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
                    if (!native.SequenceEqual(derived)) throw new InvalidDataException("Independent composed match set or full ranking differs.");
                    static long[] Diverse(IEnumerable<(long Row, string Id, string Document, double Score)> rows)
                    {
                        var counts = new Dictionary<string, int>(); var chosen = new List<long>();
                        foreach (var row in rows) { counts.TryGetValue(row.Document, out var count); if (count == 3) continue; counts[row.Document] = count + 1; chosen.Add(row.Row); if (chosen.Count == 50) break; }
                        return chosen.ToArray();
                    }
                    if (!Diverse(native).SequenceEqual(Diverse(derived))) throw new InvalidDataException("Exact diverse ranking differs.");
                }
                if (composed is not null) scoreRows += native.Count;
                comparisons++;
            }
            foreach (var query in queries) foreach (var filtered in new[] { false, true }) { await Check(query, filtered, cache); await Check(query, filtered, cache); await Check(query, filtered, constrained); await Check(query, filtered, evicting); }
            // Corpus statistics change even for untouched rows: generation invalidation must
            // clear every primitive before adding contributions from the new committed corpus.
            await using (var mutation = connection.CreateCommand())
            {
                mutation.CommandText = "DELETE FROM chunks_fts WHERE rowid=3; DELETE FROM chunks_fts_source WHERE rowid=3; DELETE FROM chunks WHERE rowid=3; INSERT INTO chunks VALUES(999999,'new','pending','article','indexed_partial'); INSERT INTO chunks_fts_source VALUES(999999,'red giant café telescopes','orbit','red'); INSERT INTO chunks_fts(rowid,text,context,title) VALUES(999999,'red giant café telescopes','orbit','red'); INSERT INTO chunks VALUES(1000002,'nonmatching','nonmatching','note','indexed'); INSERT INTO chunks_fts_source VALUES(1000002,$nonmatching,'',''); INSERT INTO chunks_fts(rowid,text,context,title) VALUES(1000002,$nonmatching,'','');";
                mutation.Parameters.AddWithValue("$nonmatching", string.Join(' ', Enumerable.Repeat("nonmatchingfixtureword", 1000)));
                await mutation.ExecuteNonQueryAsync(); generation++;
            }
            foreach (var query in queries) { await Check(query, true, cache); await Check(query, true, evicting); }
            if (evicting.Evictions == 0 || constrained.Fallbacks == 0 || cache.Hits == 0) throw new InvalidDataException("Memory/cold/warm cases were not exercised.");
            reports.Add(new { seed, cache_budget_bytes = 2 * 1024 * 1024, accounted_cache_peak_bytes = cache.PeakBytes, cache.Loads, cache.Hits, cache.Evictions, cache.Fallbacks,
                constrained_budget_bytes = 1024, constrained_fallbacks = constrained.Fallbacks,
                eviction_budget_bytes = 128 * 1024, eviction_peak_bytes = evicting.PeakBytes, forced_evictions = evicting.Evictions, eviction_fallbacks = evicting.Fallbacks,
                generation_invalidations = cache.GenerationInvalidations,
                working_union_entries_bound = 4097, conservative_working_union_bytes_bound = 4097 * 80,
                combined_cache_and_working_upper_bound_bytes = 2 * 1024 * 1024 + 4097 * 80,
                native_oracle_and_fixture_memory_outside_cache_budget = true });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { workload = "isolated primitive-phrase contribution proof, no production cache or query-result cache", comparisons, native_score_rows_compared = scoreRows, exact_native_double_bits_and_diverse_rank_equal = true, elapsed_seconds = timer.Elapsed.TotalSeconds, reports }));
    }
}
