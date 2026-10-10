using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Retrieval;
using SecondBrain.Storage.Retrieval;

internal static class LexicalCompare
{
    private sealed record Row(long Rowid, string Id, string Document, double Score);
    private sealed record Highlight(string Marked, string Text);
    private static readonly IComparer<Row> Order = Comparer<Row>.Create((left, right) =>
    {
        var score = left.Score.CompareTo(right.Score);
        return score != 0 ? score : StringComparer.Ordinal.Compare(left.Id, right.Id);
    });

    public static async Task RunAsync(string path, string text, string mode, string operation)
    {
        if (mode is not ("filtered" or "unfiltered") || operation is not ("plain" or "optimize"))
            throw new ArgumentException("Expected filtered/unfiltered and plain/optimize.");
        if (!Path.GetFullPath(path).Contains("lexical-copy", StringComparison.Ordinal))
            throw new ArgumentException("Experiments require an explicitly named lexical-copy fixture.");
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var repository = typeof(SqliteRetrievalRepository);
        var query = (string)repository.GetMethod("SanitizeFts", flags)!.Invoke(null, [text])!;
        var filters = new List<(string Name, object? Value)>();
        var filter = (string)repository.GetMethod("BuildFilters", flags)!.Invoke(null,
            [new RetrievalFilters(Type: mode == "filtered" ? "article" : null), filters])!;
        var select = (string)repository.GetField("Select", flags)!.GetRawConstantValue()!;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = operation == "optimize" ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        connection.DefaultTimeout = 120;
        async Task<long> Segments()
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(DISTINCT segid) FROM chunks_fts_idx";
            return (long)(await command.ExecuteScalarAsync())!;
        }
        var segmentsBefore = await Segments();
        double? optimizeMilliseconds = null;
        if (operation == "optimize")
        {
            var timer = Stopwatch.StartNew();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO chunks_fts(chunks_fts) VALUES('optimize')";
            await command.ExecuteNonQueryAsync();
            optimizeMilliseconds = timer.Elapsed.TotalMilliseconds;
        }
        var segmentsAfter = await Segments();
        await using var transaction = connection.BeginTransaction(deferred: true);
        var common = filters.Concat(new (string Name, object? Value)[]
            { ("$query", query), ("$start", "[qualification:start]"), ("$end", "[qualification:end]") }).ToArray();
        SqliteCommand Command(string sql, params (string Name, object? Value)[] extra)
        {
            var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
            foreach (var item in common.Concat(extra))
                if (sql.Contains(item.Name, StringComparison.Ordinal)) command.Parameters.AddWithValue(item.Name, item.Value ?? DBNull.Value);
            return command;
        }
        async Task<string[]> Plan(string sql, params (string Name, object? Value)[] extra)
        {
            await using var command = Command("EXPLAIN QUERY PLAN " + sql, extra);
            await using var reader = await command.ExecuteReaderAsync();
            var lines = new List<string>();
            while (await reader.ReadAsync()) lines.Add(reader.GetString(3));
            return lines.ToArray();
        }
        var rankSql = "SELECT c.rowid,c.id,c.document_id,bm25(chunks_fts) FROM chunks c JOIN indexed_documents d ON d.document_id=c.document_id JOIN chunks_fts ON chunks_fts.rowid=c.rowid WHERE chunks_fts MATCH $query AND " + filter + " ORDER BY bm25(chunks_fts),c.id LIMIT $prefix";
        async Task<List<Row>> OriginalRank()
        {
            var prefix = 256;
            while (true)
            {
                var result = new List<Row>(); var counts = new Dictionary<string, int>(StringComparer.Ordinal); var scanned = 0;
                await using var command = Command(rankSql, ("$prefix", prefix));
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    scanned++;
                    var row = new Row(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3));
                    counts.TryGetValue(row.Document, out var count);
                    if (count == 3) continue;
                    counts[row.Document] = count + 1; result.Add(row);
                    if (result.Count == 50) break;
                }
                if (result.Count == 50 || scanned < prefix) return result;
                prefix = checked(prefix * 4);
            }
        }
        var streamSql = "SELECT c.rowid,c.id,c.document_id,chunks_fts.rank FROM chunks_fts CROSS JOIN chunks c ON c.rowid=chunks_fts.rowid JOIN indexed_documents d ON d.document_id=c.document_id WHERE chunks_fts MATCH $query AND " + filter + " ORDER BY chunks_fts.rank";
        async Task<(List<Row> Rows, int Scanned)> RankStream()
        {
            var selected = new SortedSet<Row>(Order); var groups = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
            var scanned = 0;
            void Remove(Row row)
            {
                selected.Remove(row); var group = groups[row.Document]; group.Remove(row);
                if (group.Count == 0) groups.Remove(row.Document);
            }
            await using var command = Command(streamSql);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                scanned++;
                var score = reader.GetDouble(3);
                // Finish the complete cutoff-score tie. Arbitrary LIMIT would discard
                // canonically earlier IDs on a heavily tied corpus.
                if (selected.Count == 50 && score > selected.Max!.Score) break;
                var row = new Row(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), score);
                if (selected.Count == 50 && Order.Compare(row, selected.Max!) >= 0) continue;
                if (groups.TryGetValue(row.Document, out var existing) && existing.Count == 3)
                {
                    var worst = existing.MaxBy(item => item, Order)!;
                    if (Order.Compare(row, worst) >= 0) continue;
                    Remove(worst);
                }
                if (selected.Count == 50) Remove(selected.Max!);
                selected.Add(row);
                if (!groups.TryGetValue(row.Document, out var group)) groups[row.Document] = group = [];
                group.Add(row);
            }
            return (selected.ToList(), scanned);
        }
        var timerRank = Stopwatch.StartNew(); var oracle = await OriginalRank(); var originalRankMilliseconds = timerRank.Elapsed.TotalMilliseconds;
        timerRank.Restart(); var streamed = await RankStream(); var streamMilliseconds = timerRank.Elapsed.TotalMilliseconds;
        if (!oracle.SequenceEqual(streamed.Rows)) throw new InvalidDataException("Rank-stream rows/scores differ from exact original diversity/tie order.");
        var pairedRanking = new List<object>();
        for (var repeat = 0; repeat < 3; repeat++)
        {
            timerRank.Restart(); var ranked = await OriginalRank(); var rankedTime = timerRank.Elapsed.TotalMilliseconds;
            timerRank.Restart(); var stream = await RankStream(); var streamedTime = timerRank.Elapsed.TotalMilliseconds;
            if (!ranked.SequenceEqual(stream.Rows)) throw new InvalidDataException("Warm paired ranking differs.");
            pairedRanking.Add(new { original_milliseconds = rankedTime, rank_stream_milliseconds = streamedTime });
        }
        var nativeRankSql = "SELECT rowid,rank FROM chunks_fts WHERE chunks_fts MATCH $query ORDER BY rank";
        timerRank.Restart();
        await using (var nativeRank = Command(nativeRankSql))
        await using (var nativeReader = await nativeRank.ExecuteReaderAsync())
        {
            for (var i = 0; i < 50 && await nativeReader.ReadAsync(); i++) { }
        }
        var nativeFirst50Milliseconds = timerRank.Elapsed.TotalMilliseconds;
        long corpusLow, corpusHigh;
        await using (var boundsCommand = Command("SELECT MIN(rowid),MAX(rowid) FROM chunks"))
        await using (var boundsReader = await boundsCommand.ExecuteReaderAsync())
        {
            await boundsReader.ReadAsync(); corpusLow = boundsReader.GetInt64(0); corpusHigh = boundsReader.GetInt64(1);
        }
        var boundedNativeRanks = new List<object>();
        foreach (var partitions in new[] { 8, 24 })
        {
            var high = corpusLow + (corpusHigh - corpusLow + 1) / partitions - 1;
            var sql = "SELECT rowid,rank FROM chunks_fts WHERE chunks_fts MATCH $query AND rowid>=$partitionlow AND rowid<=$partitionhigh ORDER BY rank";
            var values = new[] { ("$partitionlow", (object?)corpusLow), ("$partitionhigh", (object?)high) };
            var times = new List<double>();
            for (var repeat = 0; repeat < 3; repeat++)
            {
                timerRank.Restart();
                await using var boundedRank = Command(sql, values);
                await using var boundedReader = await boundedRank.ExecuteReaderAsync();
                for (var i = 0; i < 50 && await boundedReader.ReadAsync(); i++) { }
                times.Add(timerRank.Elapsed.TotalMilliseconds);
            }
            boundedNativeRanks.Add(new { partitions, milliseconds = times, plan = await Plan(sql, values) });
        }
        var parallelRanks = new List<object>();
        foreach (var partitions in new[] { 7, 8, 12 })
        {
            var times = new List<double>();
            for (var repeat = 0; repeat < 3; repeat++)
            {
                timerRank.Restart();
                var localResults = await Task.WhenAll(Enumerable.Range(0, partitions).Select(partition => Task.Run(async () =>
                {
                    await using var local = new SqliteConnection(connection.ConnectionString); await local.OpenAsync();
                    await using var localTransaction = local.BeginTransaction(deferred: true);
                    var low = corpusLow + (corpusHigh - corpusLow + 1) * partition / partitions;
                    var high = corpusLow + (corpusHigh - corpusLow + 1) * (partition + 1) / partitions - 1;
                    var prefix = 256;
                    while (true)
                    {
                        var result = new List<Row>(); var groups = new Dictionary<string, int>(StringComparer.Ordinal); var scanned = 0;
                        await using var command = local.CreateCommand(); command.Transaction = localTransaction;
                        command.CommandText = rankSql.Replace(" ORDER BY", " AND chunks_fts.rowid>=$partitionlow AND chunks_fts.rowid<=$partitionhigh ORDER BY", StringComparison.Ordinal);
                        foreach (var item in common.Concat(new (string Name, object? Value)[] { ("$prefix", prefix), ("$partitionlow", low), ("$partitionhigh", high) }))
                            if (command.CommandText.Contains(item.Name, StringComparison.Ordinal)) command.Parameters.AddWithValue(item.Name, item.Value ?? DBNull.Value);
                        await using var reader = await command.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            scanned++; var row = new Row(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3));
                            groups.TryGetValue(row.Document, out var count); if (count >= 3) continue;
                            groups[row.Document] = count + 1; result.Add(row); if (result.Count == 50) break;
                        }
                        if (result.Count == 50 || scanned < prefix) return result;
                        prefix = checked(prefix * 4);
                    }
                })));
                var merged = new List<Row>(); var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var row in localResults.SelectMany(rows => rows).Order(Order))
                {
                    counts.TryGetValue(row.Document, out var count); if (count >= 3) continue;
                    counts[row.Document] = count + 1; merged.Add(row); if (merged.Count == 50) break;
                }
                times.Add(timerRank.Elapsed.TotalMilliseconds);
                if (!oracle.SequenceEqual(merged)) throw new InvalidDataException("Partitioned ranking changed exact scores/IDs/diversity.");
            }
            parallelRanks.Add(new { partitions, milliseconds = times, exact_rank_ids_scores_and_diversity_equal = true });
        }
        var oldSql = select.Replace(" FROM chunks c", ",highlight(chunks_fts,0,$start,$end),f.text,c.start_offset,c.end_offset FROM chunks c", StringComparison.Ordinal) +
            " JOIN chunks_fts ON chunks_fts.rowid=c.rowid JOIN chunks_fts_source f ON f.rowid=c.rowid WHERE chunks_fts MATCH $query AND c.id=$id";
        var timerHighlight = Stopwatch.StartNew(); var originals = new Dictionary<long, Highlight>();
        foreach (var row in oracle)
        {
            await using var command = Command(oldSql, ("$id", row.Id));
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new InvalidDataException("Original highlight is missing.");
            originals.Add(row.Rowid, new(reader.GetString(14), reader.GetString(15)));
        }
        var originalHighlightMilliseconds = timerHighlight.Elapsed.TotalMilliseconds;
        var combinedRankHighlights = new List<object>();
        foreach (var partitions in new[] { 1, 8, 12 })
        {
            var combinedSql = rankSql.Replace("bm25(chunks_fts) FROM", "bm25(chunks_fts),highlight(chunks_fts,0,$start,$end) FROM", StringComparison.Ordinal)
                .Replace(" ORDER BY", " AND chunks_fts.rowid>=$partitionlow AND chunks_fts.rowid<=$partitionhigh ORDER BY", StringComparison.Ordinal);
            var values = new[] { ("$prefix", (object?)256), ("$partitionlow", (object?)corpusLow), ("$partitionhigh", (object?)(corpusLow + (corpusHigh - corpusLow + 1) / partitions - 1)) };
            var measured = new List<double>(); var found = new Dictionary<long, string>();
            for (var repeat = 0; repeat < 3; repeat++)
            {
                found.Clear(); timerHighlight.Restart();
                await using var command = Command(combinedSql, values);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) found.Add(reader.GetInt64(0), reader.GetString(4));
                measured.Add(timerHighlight.Elapsed.TotalMilliseconds);
            }
            foreach (var item in originals)
                if (found.TryGetValue(item.Key, out var marked) && marked != item.Value.Marked)
                    throw new InvalidDataException("Combined native rank highlight differs.");
            combinedRankHighlights.Add(new { partitions, milliseconds = measured, native_highlights_compared = originals.Keys.Count(found.ContainsKey), plan = await Plan(combinedSql, values) });
        }
        var wanted = oracle.Select((row, i) => (Name: "$wanted" + i, Value: (object?)row.Rowid)).ToArray();
        var bounds = new (string Name, object? Value)[] { ("$low", oracle.Min(row => row.Rowid)), ("$high", oracle.Max(row => row.Rowid)) };
        var rowids = string.Join(',', wanted.Select(item => item.Name));
        var batchSql = "SELECT chunks_fts.rowid,highlight(chunks_fts,0,$start,$end),f.text FROM chunks_fts CROSS JOIN chunks_fts_source f ON f.rowid=chunks_fts.rowid WHERE chunks_fts MATCH $query AND chunks_fts.rowid>=$low AND chunks_fts.rowid<=$high AND +chunks_fts.rowid IN (" + rowids + ") ORDER BY chunks_fts.rowid";
        var plainInSql = batchSql.Replace("+chunks_fts.rowid IN", "chunks_fts.rowid IN", StringComparison.Ordinal);
        var highlightResults = new List<object>();
        foreach (var (name, sql) in new[] { ("plain_rowid_in", plainInSql), ("single_cursor_bounded_selected_rows", batchSql) })
        {
            var measured = new List<double>();
            for (var repeat = 0; repeat < 3; repeat++)
            {
                timerHighlight.Restart(); var found = new Dictionary<long, Highlight>();
                await using var command = Command(sql, wanted.Concat(bounds).ToArray());
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) found.Add(reader.GetInt64(0), new(reader.GetString(1), reader.GetString(2)));
                measured.Add(timerHighlight.Elapsed.TotalMilliseconds);
                if (found.Count != originals.Count || originals.Any(item => !found.TryGetValue(item.Key, out var highlight) || highlight != item.Value))
                    throw new InvalidDataException("Batched highlights/search text differ from original SQLite highlight output.");
            }
            highlightResults.Add(new { name, sql, milliseconds = measured, plan = await Plan(sql, wanted.Concat(bounds).ToArray()), exact_highlights_and_search_text_equal = true });
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            workload = "copied-index lexical-only native SQLite microcomparison; no production fixture mutation or latency qualification",
            query_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), filtered = mode == "filtered", operation,
            segments_before = segmentsBefore, segments_after = segmentsAfter, optimize_milliseconds = optimizeMilliseconds,
            selected_chunks = oracle.Count, selected_min_rowid = oracle.Min(row => row.Rowid), selected_max_rowid = oracle.Max(row => row.Rowid),
            original_rank_milliseconds = originalRankMilliseconds, original_rank_plan = await Plan(rankSql, ("$prefix", 256)),
            rank_stream_milliseconds = streamMilliseconds, rank_stream_rows_scanned = streamed.Scanned,
            rank_stream_plan = await Plan(streamSql), exact_rank_ids_scores_and_diversity_equal = true,
            paired_warm_rankings = pairedRanking, native_rank_first_50_milliseconds = nativeFirst50Milliseconds,
            bounded_native_ranks = boundedNativeRanks,
            copied_fixture_parallel_ranks = parallelRanks,
            original_highlight_milliseconds = originalHighlightMilliseconds, original_highlight_plan = await Plan(oldSql, ("$id", oracle[0].Id)),
            combined_rank_highlights = combinedRankHighlights,
            batched_highlights = highlightResults,
        }));
    }
}
