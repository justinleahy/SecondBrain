using System.Data.Common;
using System.Text.Json;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Limits;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class PurgeAndAccountingSecurityTests
{
    private static readonly IngestionAuthority Session = new("purge-session", 1, 1, "purge fixture", "session");
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    [Fact]
    public async Task PurgeRemovesEveryOverrideKindAndLeavesOtherDocumentsIntact()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores); using var journal = new MutationJournal(stores.State, root.Path);
        using var files = new ManagedDocumentFiles(root.Path, journal);
        var documents = Documents(stores, files);
        var purged = await documents.CaptureAsync(new("purged body", "Purged"), Session);
        var kept = await documents.CaptureAsync(new("kept body", "Kept"), Session);
        foreach (var captured in new[] { purged, kept })
            await documents.MutateAsync(captured.Document.Id, 1, "update", JsonSerializer.SerializeToElement(new { title = "private authored title", type = "note", props = new { }, tags = new[] { "secret-tag" } }), Session);
        Assert.Equal(4L, await CountAsync(stores.State, $"SELECT COUNT(*) FROM overrides WHERE document_id='{purged.Document.Id}'"));
        await documents.MutateAsync(purged.Document.Id, 2, "purge", Empty, Session);
        Assert.Equal(0L, await CountAsync(stores.State, $"SELECT COUNT(*) FROM overrides WHERE document_id='{purged.Document.Id}'"));
        Assert.Equal(4L, await CountAsync(stores.State, $"SELECT COUNT(*) FROM overrides WHERE document_id='{kept.Document.Id}'"));
        Assert.Equal("private authored title", (await documents.GetAsync(kept.Document.Id))!.Title);
        Assert.Equal(0L, await CountAsync(stores.State, "SELECT COUNT(*) FROM meta WHERE key LIKE 'purge_cleanup:%'"));
    }

    [Fact]
    public async Task PurgeFailsClosedWhenAnOwnedCopyCannotBeStaged()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores); using var journal = new MutationJournal(stores.State, root.Path);
        using var files = new ManagedDocumentFiles(root.Path, journal);
        var documents = Documents(stores, files);
        var id = (await documents.CaptureAsync(new("linked owned copy body", "Linked"), Session)).Document.Id;
        var stray = Path.Combine(root.Path, "vault", "originals", "stray", id + ".md");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        await File.WriteAllTextAsync(stray, "linked owned copy body");
        Assert.Equal(0, Link(stray, Path.Combine(root.Path, "second-link")));
        await Assert.ThrowsAsync<InvalidDataException>(() => documents.MutateAsync(id, 1, "purge", Empty, Session).AsTask());
        Assert.True(File.Exists(stray));
        Assert.NotEqual("purged", (string)(await MigrationTestSql.ScalarAsync(stores.State, $"SELECT status FROM documents WHERE id='{id}'"))!);
        File.Delete(Path.Combine(root.Path, "second-link"));
        Assert.Equal("purged", (await documents.MutateAsync(id, 1, "purge", Empty, Session)).Document.Status);
        Assert.False(File.Exists(stray));
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string existing, string link);

    [Fact]
    public async Task PurgeInterruptedBeforeJournalFinalizationRecoversOverridesCachesAndTracesOnRestart()
    {
        using var root = new MigrationTestDataRoot();
        string id;
        await using (var stores = new MigrationTestStores(root.Path))
        {
            await SeedAsync(stores);
            using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
            var documents = Documents(stores, files);
            id = (await documents.CaptureAsync(new("interrupted purge body", "Interrupted"), Session)).Document.Id;
            await documents.MutateAsync(id, 1, "update", JsonSerializer.SerializeToElement(new { title = "private authored title", tags = new[] { "secret-tag" } }), Session);
            await EmbedAsync(stores);
            await TraceAsync(stores, id);
            using var crashingJournal = new MutationJournal(stores.State, root.Path, new CrashAt(CrashPointNames.BeforeFinalize));
            using var crashingFiles = new ManagedDocumentFiles(root.Path, crashingJournal);
            await Assert.ThrowsAsync<InjectedFailure>(() => Documents(stores, crashingFiles).MutateAsync(id, 2, "purge", Empty, Session).AsTask());
            Assert.NotEqual(0L, await CountAsync(stores.State, $"SELECT COUNT(*) FROM overrides WHERE document_id='{id}'"));
        }
        for (var restart = 0; restart < 2; restart++)
        {
            await using var stores = new MigrationTestStores(root.Path);
            await RecoverAsync(stores, root.Path);
            Assert.Equal("purged", (string)(await MigrationTestSql.ScalarAsync(stores.State, $"SELECT status FROM documents WHERE id='{id}'"))!);
            await AssertNoDerivedResidueAsync(stores, id);
        }
    }

    [Theory]
    [InlineData(CrashPointNames.AfterStatePublishing)]
    [InlineData(CrashPointNames.AfterIndexCommit)]
    public async Task CrashAfterDurablePurgeCompletesDerivedCleanupOnEveryRestart(string point)
    {
        using var root = new MigrationTestDataRoot();
        string id;
        await using (var stores = new MigrationTestStores(root.Path))
        {
            await SeedAsync(stores);
            using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
            id = (await Documents(stores, files).CaptureAsync(new("crash window body", "Window"), Session)).Document.Id;
            await EmbedAsync(stores);
            await TraceAsync(stores, id);
            var hash = (string)(await MigrationTestSql.ScalarAsync(stores.State, $"SELECT content_hash FROM documents WHERE id='{id}'"))!;
            await MigrationTestSql.WriteAsync(stores.Index, $"INSERT INTO extraction_cache(content_hash,extractor,extractor_version,type,schema_version,path,created_at) VALUES('{hash}','text',1,'note',1,'payload','now')");
            // The purge is durable (journal finalized, purged status committed) before derived cleanup can run.
            await Assert.ThrowsAsync<InjectedFailure>(() => Documents(stores, files, new CrashAt(point)).MutateAsync(id, 1, "purge", Empty, Session).AsTask());
            Assert.Equal("purged", (string)(await MigrationTestSql.ScalarAsync(stores.State, $"SELECT status FROM documents WHERE id='{id}'"))!);
            Assert.Equal(1L, await CountAsync(stores.State, $"SELECT COUNT(*) FROM meta WHERE key='purge_cleanup:{id}'"));
            Assert.NotEqual(0L, await CountAsync(stores.Index, "SELECT COUNT(*) FROM embedding_cache"));
            Assert.Equal(1L, await CountAsync(stores.Index, "SELECT COUNT(*) FROM retrieval_traces"));
            // An ordinary retry is refused once the document is purged, so only recovery can finish the cleanup.
            await Assert.ThrowsAsync<IngestionConflictException>(() => Documents(stores, files).MutateAsync(id, 2, "purge", Empty, Session).AsTask());
        }
        for (var restart = 0; restart < 2; restart++)
        {
            await using var stores = new MigrationTestStores(root.Path);
            await RecoverAsync(stores, root.Path);
            await AssertNoDerivedResidueAsync(stores, id);
            Assert.Equal(0L, await CountAsync(stores.Index, "SELECT COUNT(*) FROM extraction_cache"));
        }
    }

    [Fact]
    public async Task DeleteThenPurgeReclaimsEveryRevisionsCacheButKeepsInputsOtherDocumentsUse()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores); using var journal = new MutationJournal(stores.State, root.Path);
        using var files = new ManagedDocumentFiles(root.Path, journal);
        var documents = Documents(stores, files);
        var purged = (await documents.CaptureAsync(new("first revision astronomy", "Shared title"), Session)).Document.Id;
        await EmbedAsync(stores);
        var firstRevision = await HashesAsync(stores, purged);
        await documents.MutateAsync(purged, 1, "update", JsonSerializer.SerializeToElement(new { content = "second revision astronomy" }), Session);
        await EmbedAsync(stores);
        var secondRevision = await HashesAsync(stores, purged);
        // Another live document embeds an identical input; deleting one more keeps its own cache for restore.
        var sharing = (await documents.CaptureAsync(new("second revision astronomy", "Shared title"), Session)).Document.Id;
        var trashed = (await documents.CaptureAsync(new("trashed but restorable", "Trashed"), Session)).Document.Id;
        await EmbedAsync(stores);
        Assert.Equal(secondRevision, await HashesAsync(stores, sharing));
        var trashedHashes = await HashesAsync(stores, trashed);
        Assert.Empty(firstRevision.Intersect(secondRevision));
        await documents.MutateAsync(purged, 2, "delete", Empty, Session);
        await documents.MutateAsync(trashed, 1, "delete", Empty, Session);
        Assert.Equal(0L, await CountAsync(stores.Index, $"SELECT COUNT(*) FROM chunks WHERE document_id='{purged}'"));
        await documents.MutateAsync(purged, 3, "purge", Empty, Session);
        var cached = (await MigrationTestSql.FirstColumnAsync(stores.Index, "SELECT input_hash FROM embedding_cache")).ToHashSet();
        Assert.Empty(firstRevision.Intersect(cached));
        Assert.Subset(cached, secondRevision.ToHashSet());
        Assert.Subset(cached, trashedHashes.ToHashSet());
        Assert.Equal(0L, await CountAsync(stores.Index, $"SELECT COUNT(*) FROM embedding_cache_owners WHERE document_id='{purged}'"));
        Assert.NotEqual(0L, await CountAsync(stores.Index, $"SELECT COUNT(*) FROM embedding_cache_owners WHERE document_id='{trashed}'"));
    }

    [Fact]
    public async Task ExistingStoresReconcileUnattributableCachesAndOverridesOfAlreadyPurgedDocuments()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores); using var journal = new MutationJournal(stores.State, root.Path);
        using var files = new ManagedDocumentFiles(root.Path, journal);
        var documents = Documents(stores, files);
        var live = (await documents.CaptureAsync(new("live corpus text", "Live"), Session)).Document.Id;
        var purged = (await documents.CaptureAsync(new("already purged text", "Old purge"), Session)).Document.Id;
        await EmbedAsync(stores);
        await documents.MutateAsync(purged, 1, "purge", Empty, Session);
        // Recreate state written before lineage existed: an orphan vector, no owners, live overrides, no markers.
        await MigrationTestSql.WriteAsync(stores.Index, "INSERT INTO embedding_cache(space_id,input_hash,vector,created_at) SELECT space_id,'legacy-orphan',vector,created_at FROM embedding_cache LIMIT 1");
        await MigrationTestSql.WriteAsync(stores.Index, "DELETE FROM embedding_cache_owners; DELETE FROM index_meta WHERE key='embedding_cache_lineage'");
        await MigrationTestSql.WriteAsync(stores.State, $"INSERT INTO overrides(document_id,kind,key,value_json,created_at) VALUES('{purged}','title','','\"private authored title\"','now'); DELETE FROM meta WHERE key='purge_cleanup_backfill'");
        await TraceAsync(stores, purged);
        await stores.Runner.MigrateAsync();
        Assert.Equal(1, await PurgeCleanup.RecoverAsync(stores.State, stores.Index));
        Assert.Equal(0L, await CountAsync(stores.Index, "SELECT COUNT(*) FROM embedding_cache WHERE input_hash='legacy-orphan'"));
        Assert.Equal(await HashesAsync(stores, live), (await MigrationTestSql.FirstColumnAsync(stores.Index, $"SELECT input_hash FROM embedding_cache_owners WHERE document_id='{live}' ORDER BY input_hash")).ToArray());
        Assert.Subset((await MigrationTestSql.FirstColumnAsync(stores.Index, "SELECT input_hash FROM embedding_cache")).ToHashSet(), (await HashesAsync(stores, live)).ToHashSet());
        Assert.Equal(0L, await CountAsync(stores.State, $"SELECT COUNT(*) FROM overrides WHERE document_id='{purged}'"));
        Assert.Equal(0L, await CountAsync(stores.Index, "SELECT COUNT(*) FROM retrieval_traces"));
        Assert.Equal(0, await PurgeCleanup.RecoverAsync(stores.State, stores.Index));
    }

    [Fact]
    public async Task AccountingRefreshesNeverUseTheStateWriterAndConcurrentCallersCoalesce()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        var gated = new GatedStateStore(stores.State);
        var controller = new AdmissionController(new MutableOptions(), TimeProvider.System, new UnlimitedDisk());
        var accounting = new PersistentAdmissionAccounting(gated, controller);
        // Hold the state writer for the whole flood: a refresh that needed it could not complete.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = stores.State.QueueWriteAsync(async (_, _, _) => { held.SetResult(); await release.Task; return 0; }).AsTask();
        await held.Task;
        try
        {
            gated.Block();
            var first = accounting.RefreshAsync().AsTask();
            await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var flood = Enumerable.Range(0, 256).Select(_ => accounting.RefreshAsync().AsTask()).ToArray();
            gated.Unblock();
            var all = Task.WhenAll(flood.Append(first));
            Assert.Same(all, await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10))));
            Assert.Equal(2, gated.Reads);
            Assert.Equal(0, gated.Writes);
        }
        finally { gated.Unblock(); release.TrySetResult(); await writer; }
    }

    [Fact]
    public async Task CoalescedAccountingStillObservesWritesCommittedBeforeTheCall()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        var gated = new GatedStateStore(stores.State);
        var controller = new AdmissionController(new MutableOptions(), TimeProvider.System, new UnlimitedDisk());
        var accounting = new PersistentAdmissionAccounting(gated, controller);
        Assert.Equal(new AdmissionSnapshot(0, 0, 0), await accounting.RefreshAsync());
        gated.Block();
        var stale = accounting.RefreshAsync().AsTask();
        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var captured = await Documents(stores).CaptureAsync(new("accounted body", "Accounted"), Session);
        var fresh = accounting.RefreshAsync().AsTask();
        gated.Unblock();
        await stale;
        var snapshot = await fresh;
        Assert.Equal(1, snapshot.QueuedJobs);
        Assert.Equal((long)(await MigrationTestSql.ScalarAsync(stores.State, $"SELECT size_bytes FROM documents WHERE id='{captured.Document.Id}'"))!, snapshot.AdmittedBytes);
        Assert.Equal(new AdmissionSnapshot(1, snapshot.AdmittedBytes, 0), controller.Snapshot);
        await Documents(stores).MutateAsync(captured.Document.Id, 1, "delete", Empty, Session);
        Assert.Equal(0, (await accounting.RefreshAsync()).AdmittedBytes);
    }

    private static async Task RecoverAsync(MigrationTestStores stores, string dataRoot)
    {
        await stores.Runner.MigrateAsync();
        using var journal = new MutationJournal(stores.State, dataRoot);
        await journal.RecoverAsync();
        await new PublicationCoordinator(stores.State, stores.Index).RecoverAsync();
        await PurgeCleanup.RecoverAsync(stores.State, stores.Index);
    }

    private static async Task AssertNoDerivedResidueAsync(MigrationTestStores stores, string id)
    {
        Assert.Equal(0L, await CountAsync(stores.State, $"SELECT COUNT(*) FROM overrides WHERE document_id='{id}'"));
        Assert.Equal(0L, await CountAsync(stores.State, "SELECT COUNT(*) FROM meta WHERE key LIKE 'purge_cleanup:%'"));
        Assert.Equal(0L, await CountAsync(stores.Index, "SELECT COUNT(*) FROM embedding_cache"));
        Assert.Equal(0L, await CountAsync(stores.Index, "SELECT COUNT(*) FROM embedding_cache_owners"));
        Assert.Equal(0L, await CountAsync(stores.Index, "SELECT COUNT(*) FROM retrieval_traces"));
    }

    private static async Task EmbedAsync(MigrationTestStores stores)
    {
        var embeddings = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var space = await embeddings.EnsureSpaceAsync("purge-space", "fixture", "fixture", 2);
        foreach (var work in await embeddings.PendingAsync(space.Id, 100))
            Assert.True(await embeddings.SaveAsync(space, work, work.Chunks.ToDictionary(chunk => chunk.Id, _ => new float[] { 1, 0 })));
    }

    private static Task TraceAsync(MigrationTestStores stores, string id) =>
        MigrationTestSql.WriteAsync(stores.Index, $"INSERT INTO retrieval_traces(at,query_json,trace_json) VALUES('now','{{}}','{{\"hits\":[\"{id}\"]}}')");

    private static async Task<string[]> HashesAsync(MigrationTestStores stores, string id) =>
        (await MigrationTestSql.FirstColumnAsync(stores.Index, $"SELECT DISTINCT input_hash FROM chunks WHERE document_id='{id}' ORDER BY input_hash")).ToArray();

    private static async Task<long> CountAsync(IStoreHandle store, string sql) => (long)(await MigrationTestSql.ScalarAsync(store, sql))!;

    private static SqliteDocumentRepository Documents(MigrationTestStores stores, ManagedDocumentFiles? files = null, ICrashPoints? crash = null) =>
        new(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index, crash), files);

    private static async Task SeedAsync(MigrationTestStores stores)
    {
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
        await new AuthRepository(stores.State, TimeProvider.System).AddAsync(new CredentialRecord { Id = Session.CredentialId, Name = Session.CredentialName, Kind = Session.Kind,
            Generation = 1, AccountEpoch = 1, Scopes = "read,write,admin", Verifier = "fixture", Kid = "fixture",
            SteppedUpAt = AuthTime.Format(DateTimeOffset.UtcNow), CreatedAt = AuthTime.Format(DateTimeOffset.UtcNow) });
    }

    private sealed class InjectedFailure : Exception;
    private sealed class CrashAt(string point) : ICrashPoints
    {
        public ValueTask HitAsync(string observed, CancellationToken cancellationToken = default) => observed == point ? throw new InjectedFailure() : ValueTask.CompletedTask;
    }

    private sealed class GatedStateStore(IStateStore inner) : IStateStore
    {
        private TaskCompletionSource gate = Open();
        private int reads, writes;
        public int Reads => Volatile.Read(ref reads);
        public int Writes => Volatile.Read(ref writes);
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Block() { gate = new(TaskCreationOptions.RunContinuationsAsynchronously); Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); reads = 0; writes = 0; }
        public void Unblock() => gate.TrySetResult();
        public async ValueTask<IReadConnectionLease> OpenReadConnectionAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref reads); Entered.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
            return await inner.OpenReadConnectionAsync(cancellationToken);
        }
        public ValueTask<TResult> QueueWriteAsync<TResult>(Func<DbConnection, DbTransaction, CancellationToken, ValueTask<TResult>> write, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref writes);
            return inner.QueueWriteAsync(write, cancellationToken);
        }
        private static TaskCompletionSource Open() { var open = new TaskCompletionSource(); open.SetResult(); return open; }
    }

    private sealed class MutableOptions : Microsoft.Extensions.Options.IOptionsMonitor<SecondBrain.Core.Configuration.SecondBrainOptions>
    {
        public SecondBrain.Core.Configuration.SecondBrainOptions CurrentValue { get; } = new();
        public SecondBrain.Core.Configuration.SecondBrainOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SecondBrain.Core.Configuration.SecondBrainOptions, string?> listener) => null;
    }

    private sealed class UnlimitedDisk : IDiskCapacity
    {
        public long? AvailableBytes(string path) => long.MaxValue;
    }
}
