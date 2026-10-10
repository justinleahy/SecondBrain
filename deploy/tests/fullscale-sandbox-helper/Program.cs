using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SecondBrain.Infrastructure.Extraction;
using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Security.Cryptography;
using SecondBrain.Core.Retrieval;
using SecondBrain.Storage.Retrieval;
using SecondBrain.Storage;
using System.Data.Common;

if (args.Length == 3 && args[0] == "--fts-maintenance")
{
    await FtsMaintenance.RunAsync(args.Skip(1).ToArray());
    return;
}

if (args.Length == 2 && args[0] == "--term-contribution-scale")
{
    await TermContributionScale.RunAsync(args[1]);
    return;
}

if (args.Length == 1 && args[0] == "--term-contribution-proof")
{
    await TermContributionCompare.RunAsync();
    return;
}

if (args.Length == 5 && args[0] == "--lexical-compare")
{
    await LexicalCompare.RunAsync(args[1], args[2], args[3], args[4]);
    return;
}

if (args.Length == 4 && args[0] == "--dirty-registry")
{
    var expected = int.Parse(args[3]);
    var registry = new VectorSnapshotRegistry();
    var boundedType = typeof(SqliteIndexStore).Assembly.GetType("SecondBrain.Storage.Connections.BoundedConnection", true)!;
    async Task<(DbConnection Connection, DbTransaction Transaction, SqliteConnection Native)> OpenBounded()
    {
        var native = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[1], Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await native.OpenAsync();
        var options = new SqliteStoreOptions();
        var connection = (DbConnection)Activator.CreateInstance(boundedType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [native, options, options.ReadLeaseTimeout, null], null)!;
        return (connection, await connection.BeginTransactionAsync(), native);
    }
    using var gate = await registry.EnterAsync();
    var first = await OpenBounded();
    var registryTimer = Stopwatch.StartNew();
    using var baseline = await registry.AcquireAsync(first.Connection, first.Transaction, args[2], 1024);
    var initialSeconds = registryTimer.Elapsed.TotalSeconds;
    await first.Transaction.DisposeAsync();
    boundedType.GetMethod("Detach", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(first.Connection, null);
    await first.Native.DisposeAsync();
    if (baseline.Count != expected) throw new InvalidDataException("Initial vector count differs.");
    var ids = new List<string>();
    await using (var native = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[1], Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
    {
        await native.OpenAsync(); await using var command = native.CreateCommand();
        command.CommandText = "SELECT document_id FROM indexed_documents ORDER BY document_id";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
    }
    foreach (var id in ids) registry.Committed(id);
    var rows = registry.VectorRowsRead; var statements = registry.VectorReadStatements;
    var second = await OpenBounded(); registryTimer.Restart();
    using var refreshed = await registry.AcquireAsync(second.Connection, second.Transaction, args[2], 1024);
    var refreshSeconds = registryTimer.Elapsed.TotalSeconds;
    await second.Transaction.DisposeAsync();
    boundedType.GetMethod("Detach", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(second.Connection, null);
    await second.Native.DisposeAsync();
    if (refreshed.Count != expected) throw new InvalidDataException("Refreshed vector count differs.");
    foreach (var entry in baseline.Entries)
        if (!refreshed.TryGetVector(entry.Key, out var vector) || !entry.Value.Span.SequenceEqual(vector.Span))
            throw new InvalidDataException("Dirty refresh changed an exact live vector or chunk locator.");
    foreach (var id in ids)
        if (registry.HasMissingVectors(id) || registry.ChunkIds(id).Count != 10)
            throw new InvalidDataException("Dirty refresh changed document coverage.");
    Console.WriteLine(JsonSerializer.Serialize(new { workload = "loaded registry then Committed(documentId) for every persisted document under registry gate; read-only exact post-publication invalidation refresh, not another rebuild",
        documents = ids.Count, vectors = refreshed.Count, initial_load_seconds = initialSeconds, dirty_refresh_seconds = refreshSeconds,
        dirty_refresh_vector_rows = registry.VectorRowsRead - rows, dirty_refresh_read_statements = registry.VectorReadStatements - statements,
        default_read_lease_seconds = 15, exact_all_vectors_chunk_ids_and_coverage_equal = true, database_writes = 0,
        loaded_assemblies = new[] { typeof(VectorSnapshotRegistry).Assembly, typeof(VectorSlotMap).Assembly, Assembly.GetExecutingAssembly() }
            .Select(assembly => new { path = assembly.Location, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant() }) }));
    return;
}

if (args.Length == 4 && args[0] == "--explain")
{
    if (args[3] is not ("filtered" or "unfiltered")) throw new ArgumentException("Expected filtered or unfiltered.");
    var repository = typeof(SqliteRetrievalRepository);
    var binding = BindingFlags.Static | BindingFlags.NonPublic;
    var query = (string)repository.GetMethod("SanitizeFts", binding)!.Invoke(null, [args[2]])!;
    var filterParameters = new List<(string Name, object? Value)>();
    var filters = new RetrievalFilters(Type: args[3] == "filtered" ? "article" : null);
    var filter = (string)repository.GetMethod("BuildFilters", binding)!.Invoke(null, [filters, filterParameters])!;
    var select = (string)repository.GetField("Select", binding)!.GetRawConstantValue()!;
    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[1], Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await using var sample = connection.CreateCommand();
    sample.Transaction = (SqliteTransaction)transaction;
    sample.CommandText = "SELECT c.id,c.rowid FROM chunks c JOIN indexed_documents d ON d.document_id=c.document_id WHERE " + filter + " ORDER BY c.id LIMIT 1";
    foreach (var parameter in filterParameters) sample.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
    await using var sampleReader = await sample.ExecuteReaderAsync();
    if (!await sampleReader.ReadAsync()) throw new InvalidDataException("No committed chunk for representative plan.");
    var chunkId = sampleReader.GetString(0); var rowid = sampleReader.GetInt64(1);
    await sampleReader.DisposeAsync();
    var parameters = filterParameters.Concat(new (string Name, object? Value)[] { ("$query", query), ("$prefix", 256), ("$id", chunkId), ("$rowid", rowid), ("$start", "[qualification:start]"), ("$end", "[qualification:end]") }).ToArray();
    var rank = "SELECT c.id,c.document_id FROM chunks c JOIN indexed_documents d ON d.document_id=c.document_id JOIN chunks_fts ON chunks_fts.rowid=c.rowid WHERE chunks_fts MATCH $query AND " + filter + " ORDER BY bm25(chunks_fts),c.id LIMIT $prefix";
    var highlight = select.Replace(" FROM chunks c", ",highlight(chunks_fts,0,$start,$end),f.text,c.start_offset,c.end_offset FROM chunks c", StringComparison.Ordinal) +
        " JOIN chunks_fts ON chunks_fts.rowid=c.rowid JOIN chunks_fts_source f ON f.rowid=c.rowid WHERE chunks_fts MATCH $query AND c.id=$id";
    var queries = new Dictionary<string, string> { ["current_lexical_rank"] = rank, ["current_highlight_chunk_id"] = highlight,
        ["highlight_explicit_fts_rowid"] = highlight.Replace("c.id=$id", "chunks_fts.rowid=$rowid", StringComparison.Ordinal),
        ["highlight_explicit_fts_rowid_and_chunk_id"] = highlight + " AND chunks_fts.rowid=$rowid" };
    var plans = new List<object>();
    foreach (var item in queries)
    {
        await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "EXPLAIN QUERY PLAN " + item.Value;
        foreach (var parameter in parameters.Where(parameter => item.Value.Contains(parameter.Name, StringComparison.Ordinal)))
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object>();
        while (await reader.ReadAsync()) rows.Add(new { id = reader.GetInt64(0), parent = reader.GetInt64(1), detail = reader.GetString(3) });
        plans.Add(new { name = item.Key, sql = item.Value, rows });
    }
    Console.WriteLine(JsonSerializer.Serialize(new { mode = "read-only EXPLAIN; actual repository sanitizer/filter/select contracts", filtered = args[3] == "filtered",
        query_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(args[2]))), representative_chunk_id = chunkId, representative_chunk_rowid = rowid, plans }));
    return;
}

if (args.Length == 2 && args[0] == "--read-traces")
{
    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[1], Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT trace_json FROM retrieval_traces ORDER BY id";
    await using var reader = await command.ExecuteReaderAsync();
    var traces = new List<string>();
    while (await reader.ReadAsync()) traces.Add(reader.GetString(0));
    Console.WriteLine(JsonSerializer.Serialize(traces));
    return;
}

if (args.Length != 5) throw new ArgumentException("Expected extractor socket and four immutable PDF fixture paths.");
ThreadPool.GetMinThreads(out var workers, out var completion);
ThreadPool.SetMinThreads(Math.Max(workers, 4), Math.Max(completion, 4));
var client = new DescriptorExtractorClient(args[0]);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(135));
var timer = Stopwatch.StartNew();
Console.WriteLine(JsonSerializer.Serialize(new { phase = "rpc-starting", started_at_epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d }));
Console.Out.Flush();
var results = await Task.WhenAll(args.Skip(1).Select(async path =>
{
    await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    var started = DateTimeOffset.UtcNow;
    var result = await client.ExtractAsync(file, "pdf", deadline.Token);
    var finished = DateTimeOffset.UtcNow;
    if (result.Status != "extracted" || result.Pages?.Count != 80 || result.LosslessText.Length == 0)
        throw new InvalidDataException($"Direct sandbox fixture failed: status={result.Status}, pages={result.Pages?.Count}, error={result.Error}, bound={result.Bound}");
    return new { fixture = Path.GetFileName(path), result.Status, pages = result.Pages.Count,
        text_bytes = Encoding.UTF8.GetByteCount(result.LosslessText), input_bytes = file.Length,
        started_at_epoch = started.ToUnixTimeMilliseconds() / 1000d, completed_at_epoch = finished.ToUnixTimeMilliseconds() / 1000d,
        elapsed_ms = (finished - started).TotalMilliseconds };
}));
var current = long.Parse(await File.ReadAllTextAsync("/sys/fs/cgroup/memory.current"));
var peak = long.Parse(await File.ReadAllTextAsync("/sys/fs/cgroup/memory.peak"));
Console.WriteLine(JsonSerializer.Serialize(new { workload = "four concurrent immutable 80-page PDF descriptor RPCs; no daemon admission/publication",
    results, elapsed_seconds = timer.Elapsed.TotalSeconds, helper_cgroup_current_bytes = current, helper_cgroup_peak_bytes = peak,
    helper_managed_bytes = GC.GetTotalMemory(false), helper_process_id = Environment.ProcessId }));
