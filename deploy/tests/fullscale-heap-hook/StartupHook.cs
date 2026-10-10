using System.Diagnostics;
using System.Text.Json;
using System.Reflection;
using System.Collections;
using System.Data.Common;
using System.Security.Cryptography;

/// <summary>Isolated qualification instrumentation; never included in the production application.</summary>
internal static class StartupHook
{
    private static Timer? sampler;
    private static long sample;
    private static int sampling;
    private static long firstHealthTicks;
    private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromMilliseconds(200) };
    public static void Initialize()
    {
        // brain serve launches the server as a child; measure only the actual server heap.
        if (!Environment.GetCommandLineArgs().Any(argument => Path.GetFileName(argument) == "SecondBrain.Server.dll")) return;
        var directory = Environment.GetEnvironmentVariable("SECONDBRAIN_FULLSCALE_HEAP_DIRECTORY");
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        _ = Task.Run(async () =>
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromMinutes(2))
            {
                try
                {
                    using var response = await HealthClient.GetAsync("http://127.0.0.1:7171/health");
                    if (response.IsSuccessStatusCode)
                    {
                        Volatile.Write(ref firstHealthTicks, DateTimeOffset.UtcNow.UtcTicks);
                        return;
                    }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(50);
            }
        });
        sampler = new Timer(_ =>
        {
            if (Interlocked.CompareExchange(ref sampling, 1, 0) != 0) return;
            try
            {
                var healthTicks = Volatile.Read(ref firstHealthTicks);
                var invalidate = Path.Combine(directory, "request-invalidate-all-documents");
                if (File.Exists(invalidate))
                {
                    File.Delete(invalidate);
                    try { InvalidateAllDocuments(directory); }
                    catch (Exception exception)
                    {
                        WriteInvalidationAcknowledgement(directory, new { success = false, error = exception.ToString() });
                    }
                }
                DateTimeOffset? firstHealth = healthTicks == 0 ? null : new DateTimeOffset(healthTicks, TimeSpan.Zero);
                // A marker requests one blocking full collection outside timed requests.
                var collect = Path.Combine(directory, "request-full-gc");
                var forced = File.Exists(collect);
                if (forced) { File.Delete(collect); GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true); }
                var info = GC.GetGCMemoryInfo();
                var process = Process.GetCurrentProcess();
                var row = new { at = DateTimeOffset.UtcNow, process_id = process.Id, process_started_at = process.StartTime.ToUniversalTime(),
                    first_health_at = firstHealth, process_to_health_seconds_upper_bound = firstHealth is null ? (double?)null : (firstHealth.Value.UtcDateTime - process.StartTime.ToUniversalTime()).TotalSeconds,
                    vector_statistics = VectorStatistics(), sample = Interlocked.Increment(ref sample), forced_full_gc = forced,
                    managed_live_bytes = GC.GetTotalMemory(false), gc_heap_size_bytes = info.HeapSizeBytes,
                    gc_fragmented_bytes = info.FragmentedBytes, gc_committed_bytes = info.TotalCommittedBytes,
                    gc_memory_load_bytes = info.MemoryLoadBytes, working_set_bytes = process.WorkingSet64 };
                File.AppendAllText(Path.Combine(directory, "heap.jsonl"), JsonSerializer.Serialize(row) + "\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { Volatile.Write(ref sampling, 0); }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => sampler.Dispose();
    }

    private static object? VectorStatistics()
    {
        var registryType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("SecondBrain.Storage.Retrieval.VectorSnapshotRegistry"))
            .FirstOrDefault(type => type is not null);
        if (registryType?.GetField("Registries", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is not IEnumerable registries) return null;
        foreach (var entry in registries)
        {
            var registry = entry.GetType().GetProperty("Value")!.GetValue(entry);
            var slots = registryType.GetField("slots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry);
            if (slots is not null) return slots.GetType().GetProperty("Statistics")!.GetValue(slots);
        }
        return null;
    }

    private static void InvalidateAllDocuments(string directory)
    {
        var registryType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("SecondBrain.Storage.Retrieval.VectorSnapshotRegistry"))
            .First(type => type is not null)!;
        var registries = (IEnumerable)registryType.GetField("Registries", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (var entry in registries)
        {
            var registry = entry.GetType().GetProperty("Value")!.GetValue(entry)!;
            var slots = registryType.GetField("slots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry);
            if (slots is null) continue;
            var index = entry.GetType().GetProperty("Key")!.GetValue(entry)!;
            var databasePath = (string)index.GetType().GetProperty("DatabasePath")!.GetValue(index)!;
            var enter = (ValueTask<IDisposable>)registryType.GetMethod("EnterAsync")!.Invoke(registry, [CancellationToken.None])!;
            using var gate = enter.GetAwaiter().GetResult();
            var connectionType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("Microsoft.Data.Sqlite.SqliteConnection"))
                .First(type => type is not null)!;
            using var connection = (DbConnection)Activator.CreateInstance(connectionType, "Data Source=" + databasePath + ";Mode=ReadOnly;Pooling=False")!;
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT document_id FROM indexed_documents ORDER BY document_id";
            var ids = new List<string>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
            if (ids.Count != 100000) throw new InvalidDataException("Expected the full100k authoritative fixture.");
            var committed = registryType.GetMethod("Committed")!;
            var started = DateTimeOffset.UtcNow;
            foreach (var id in ids) committed.Invoke(registry, [id]);
            // Deliberately do not Acquire: the next real HTTP hybrid search must refresh
            // this genuinely loaded-and-dirty registry within its own bounded lease.
            WriteInvalidationAcknowledgement(directory, new {
                success = true, documents = ids.Count, started_at = started, completed_at = DateTimeOffset.UtcNow,
                database_mode = "ReadOnly", database_writes = 0, acquire_after_invalidation = false,
                production_storage_path = registryType.Assembly.Location,
                production_storage_sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(registryType.Assembly.Location))),
                production_core_path = slots.GetType().Assembly.Location,
                production_core_sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(slots.GetType().Assembly.Location))),
                workload = "actual running daemon registry Committed(all persisted100k document IDs), gate held; first following HTTP hybrid search owns refresh" });
            return;
        }
        throw new InvalidOperationException("Actual running daemon registry has no loaded vector generation.");
    }

    private static void WriteInvalidationAcknowledgement(string directory, object value)
    {
        var path = Path.Combine(directory, "invalidated-all-documents.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
