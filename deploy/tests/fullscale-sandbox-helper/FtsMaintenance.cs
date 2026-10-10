using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Retrieval;
using SecondBrain.Storage;
using SecondBrain.Storage.Retrieval;

internal static class FtsMaintenance
{
    private sealed record Hit(long Rowid, string Id, string Document, long ScoreBits);

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 2 || !Path.GetFullPath(args[0]).Contains("lexical-copy", StringComparison.Ordinal))
            throw new ArgumentException("Expected a separate lexical-copy database and recorded question JSONL path.");
        await using var native = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = args[0], Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await native.OpenAsync();
        await using (var configure = native.CreateCommand())
        {
            configure.CommandText = "PRAGMA synchronous=NORMAL";
            await configure.ExecuteNonQueryAsync();
        }
        var boundedType = typeof(SqliteIndexStore).Assembly.GetType("SecondBrain.Storage.Connections.BoundedConnection", true)!;
        var options = new SqliteStoreOptions();
        DbConnection Bound() => (DbConnection)Activator.CreateInstance(boundedType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [native, options, options.TransactionTimeout, null], null)!;
        void Detach(DbConnection connection) => boundedType.GetMethod("Detach", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(connection, null);
        async Task<long> Scalar(string sql)
        {
            await using var command = native.CreateCommand(); command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var repository = typeof(SqliteRetrievalRepository);
        var questions = File.ReadLines(args[1]).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        async Task<List<object>> Fingerprints()
        {
            var results = new List<object>();
            foreach (var filtered in new[] { true, false })
            foreach (var question in questions)
            {
                var query = (string)repository.GetMethod("SanitizeFts", flags)!.Invoke(null, [question.GetProperty("query").GetString()!])!;
                var parameters = new List<(string Name, object? Value)>();
                var filter = (string)repository.GetMethod("BuildFilters", flags)!.Invoke(null,
                    [new RetrievalFilters(Type: filtered ? "article" : null), parameters])!;
                var bounded = Bound();
                var stage = "rank";
                try
                {
                    await using var transaction = await bounded.BeginTransactionAsync();
                    var hits = new List<Hit>(); var prefix = 256;
                    var timer = Stopwatch.StartNew();
                    while (true)
                    {
                        hits.Clear(); var scanned = 0; var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                        await using var rank = bounded.CreateCommand(); rank.Transaction = transaction;
                        rank.CommandText = "SELECT c.rowid,c.id,c.document_id,bm25(chunks_fts) FROM chunks c JOIN indexed_documents d ON d.document_id=c.document_id JOIN chunks_fts ON chunks_fts.rowid=c.rowid WHERE chunks_fts MATCH $query AND " + filter + " ORDER BY bm25(chunks_fts),c.id LIMIT $prefix";
                        rank.Parameters.Add(Parameter(rank, "$query", query)); rank.Parameters.Add(Parameter(rank, "$prefix", prefix));
                        foreach (var parameter in parameters) rank.Parameters.Add(Parameter(rank, parameter.Name, parameter.Value));
                        await using var reader = await rank.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            scanned++; var document = reader.GetString(2); counts.TryGetValue(document, out var count);
                            if (count == 3) continue;
                            counts[document] = count + 1;
                            hits.Add(new(reader.GetInt64(0), reader.GetString(1), document, BitConverter.DoubleToInt64Bits(reader.GetDouble(3))));
                            if (hits.Count == 50) break;
                        }
                        if (hits.Count == 50 || scanned < prefix) break;
                        prefix = checked(prefix * 4);
                    }
                    var rankMilliseconds = timer.Elapsed.TotalMilliseconds;
                    stage = "native batch highlight";
                    var highlights = new Dictionary<long, (string Marked, string Text)>(); timer.Restart();
                    if (hits.Count != 0)
                    {
                        await using var highlight = bounded.CreateCommand(); highlight.Transaction = transaction;
                        highlight.CommandText = "SELECT chunks_fts.rowid,highlight(chunks_fts,0,$start,$end),f.text FROM chunks_fts CROSS JOIN chunks_fts_source f ON f.rowid=chunks_fts.rowid WHERE chunks_fts MATCH $query AND chunks_fts.rowid>=$low AND chunks_fts.rowid<=$high AND +chunks_fts.rowid IN (" +
                            string.Join(',', hits.Select((_, i) => "$row" + i)) + ") ORDER BY chunks_fts.rowid";
                        foreach (var parameter in new (string, object?)[] { ("$query", query), ("$start", "[qualification:start]"), ("$end", "[qualification:end]"), ("$low", hits.Min(hit => hit.Rowid)), ("$high", hits.Max(hit => hit.Rowid)) })
                            highlight.Parameters.Add(Parameter(highlight, parameter.Item1, parameter.Item2));
                        for (var i = 0; i < hits.Count; i++) highlight.Parameters.Add(Parameter(highlight, "$row" + i, hits[i].Rowid));
                        await using var reader = await highlight.ExecuteReaderAsync();
                        while (await reader.ReadAsync()) highlights.Add(reader.GetInt64(0), (reader.GetString(1), reader.GetString(2)));
                    }
                    if (highlights.Count != hits.Count) throw new InvalidDataException("Native highlight coverage differs from rank candidates.");
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(hits.Select(hit => new { hit.Rowid, hit.Id, hit.Document, hit.ScoreBits,
                        Marked = highlights[hit.Rowid].Marked, Text = highlights[hit.Rowid].Text }));
                    results.Add(new { question_id = question.GetProperty("id").GetString(), filtered, candidates = hits.Count,
                        sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), rank_milliseconds = rankMilliseconds, native_batch_highlight_milliseconds = timer.Elapsed.TotalMilliseconds });
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException($"Fingerprint {question.GetProperty("id").GetString()} filtered={filtered} stage={stage} failed before/after maintenance.", exception);
                }
                finally { Detach(bounded); }
            }
            return results;
        }
        var segmentsBefore = await Scalar("SELECT COUNT(DISTINCT segid) FROM chunks_fts_idx");
        var fingerprintsBefore = await Fingerprints();
        var statements = new List<object>(); var total = Stopwatch.StartNew(); var maximumMilliseconds = 0d; var complete = false;
        for (var step = 0; step < 10000; step++)
        {
            var changesBefore = await Scalar("SELECT total_changes()");
            var bounded = Bound(); var timer = Stopwatch.StartNew();
            try
            {
                await using var transaction = await bounded.BeginTransactionAsync();
                await using var merge = bounded.CreateCommand(); merge.Transaction = transaction;
                merge.CommandText = "INSERT INTO chunks_fts(chunks_fts,rank) VALUES('merge',$pages)";
                merge.Parameters.Add(Parameter(merge, "$pages", step == 0 ? -64 : 64));
                await merge.ExecuteNonQueryAsync(); await transaction.CommitAsync();
            }
            finally { Detach(bounded); }
            var milliseconds = timer.Elapsed.TotalMilliseconds; maximumMilliseconds = Math.Max(maximumMilliseconds, milliseconds);
            var delta = await Scalar("SELECT total_changes()") - changesBefore;
            statements.Add(new { step, pages = step == 0 ? -64 : 64, milliseconds, total_changes_delta = delta });
            if (delta < 2) { complete = true; break; }
        }
        if (!complete) throw new InvalidDataException("Bounded maintenance did not complete within10000 independent transactions.");
        var totalMilliseconds = total.Elapsed.TotalMilliseconds;
        var segmentsAfter = await Scalar("SELECT COUNT(DISTINCT segid) FROM chunks_fts_idx");
        var fingerprintsAfter = await Fingerprints();
        static string[] OnlyFingerprints(List<object> fingerprints) => fingerprints.Select(row => JsonSerializer.SerializeToElement(row).GetProperty("sha256").GetString()!).ToArray();
        if (!OnlyFingerprints(fingerprintsBefore).SequenceEqual(OnlyFingerprints(fingerprintsAfter)))
            throw new InvalidDataException("Maintenance changed exact rank order/score bits/native highlights/search text.");
        Console.WriteLine(JsonSerializer.Serialize(new { workload = "copied database only; merge(-64) then independent merge(+64) transactions until total_changes delta<2; no optimize",
            statement_timeout_seconds = options.StatementTimeout.TotalSeconds, transaction_timeout_seconds = options.TransactionTimeout.TotalSeconds,
            segments_before = segmentsBefore, segments_after = segmentsAfter, statements, statement_count = statements.Count,
            maximum_statement_and_commit_milliseconds = maximumMilliseconds, total_maintenance_milliseconds = totalMilliseconds,
            exact_native_rank_score_bits_highlights_and_text_equal = true, fingerprints_before = fingerprintsBefore, fingerprints_after = fingerprintsAfter }));
    }

    private static DbParameter Parameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; return parameter;
    }
}
