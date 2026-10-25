using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using SecondBrain.Storage.Sources;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class FolderSourcePurgeTests
{
    private static readonly IngestionAuthority Admin = new("purge-admin", 1, 1, "admin", "session");

    [Fact]
    public async Task PurgeRemovesManagedAndTrashedCopiesAndLineageButPreservesExternalOriginals()
    {
        await using var f = await Fixture.Create();
        var first = await f.Import("first.txt", "private-first");
        var second = await f.Import("second.txt", "private-second");
        await f.Documents.MutateAsync(second.Id, second.Revision, "delete", JsonSerializer.SerializeToElement(new { }), Admin);
        await f.Folders.PurgeAsync(f.Source.Id, Admin);
        Assert.Equal("removed", await f.Scalar("SELECT status FROM sources WHERE id='folder'"));
        Assert.Equal(2L, await f.Scalar("SELECT COUNT(*) FROM documents WHERE source_id='folder' AND status='purged' AND title='[purged]'"));
        Assert.Equal("private-first", await File.ReadAllTextAsync(Path.Combine(f.External, "first.txt")));
        Assert.Equal("private-second", await File.ReadAllTextAsync(Path.Combine(f.External, "second.txt")));
        Assert.False(File.Exists(first.Path));
        Assert.Equal(0L, await f.Scalar("SELECT COUNT(*) FROM payloads WHERE instr(CAST(bytes AS TEXT),'private-first')>0 OR instr(CAST(bytes AS TEXT),'private-second')>0"));
        Assert.Equal(0L, await f.Scalar("SELECT COUNT(*) FROM folder_source_purges WHERE completed_at IS NULL"));
        await f.Folders.PurgeAsync(f.Source.Id, Admin); // completed retry does not create another revision
        Assert.Equal(1L, await f.Scalar("SELECT COUNT(*) FROM revisions WHERE document_id=@id AND cause='purge'", new { id = first.Id }));
        await Assert.ThrowsAsync<IngestionConflictException>(async () => await f.Folders.PlanScanAsync(f.Source, [], false));
    }

    [Fact]
    public async Task PreviouslyRemovedSourceAndEmptySourceCanBePurged()
    {
        await using var f = await Fixture.Create(); var document = await f.Import("old.txt", "old text");
        await f.Folders.SetStatusAsync(f.Source.Id, "removed", Admin);
        await f.Folders.PurgeAsync(f.Source.Id, Admin);
        Assert.Equal("purged", (await f.Documents.GetAsync(document.Id))!.Status);
        await f.Write("INSERT INTO sources(id,kind,name,status,created_at) VALUES('empty','folder','Empty','disabled',@now)");
        await f.Folders.PurgeAsync("empty", Admin);
        Assert.Equal(0L, await f.Scalar("SELECT COUNT(*) FROM folder_source_purges WHERE completed_at IS NULL"));
    }

    [Fact]
    public async Task UnauthorizedAndProtectedSourceRequestsHaveNoEffects()
    {
        await using var f = await Fixture.Create(); var document = await f.Import("old.txt", "old text");
        await Assert.ThrowsAsync<AuthorityChangedException>(async () => await f.Folders.PurgeAsync("folder", Admin with { CredentialGeneration = 2 }));
        await f.Write("INSERT INTO sources(id,kind,name,status,created_at) VALUES('inbox','folder','Inbox','active',@now)");
        var error = await Assert.ThrowsAsync<IngestionConflictException>(async () => await f.Folders.PurgeAsync("inbox", Admin));
        Assert.Equal("protected-source", error.Code);
        Assert.True(File.Exists(document.Path));
        Assert.Equal(0L, await f.Scalar("SELECT COUNT(*) FROM folder_source_purges"));
    }

    [Fact]
    public async Task SourcePurgeDrainsMoreThanOneThousandOwnedFilesInBoundedBatches()
    {
        await using var f = await Fixture.Create(); var document = await f.Import("many.txt", "many copies");
        var history = Directory.CreateDirectory(Path.Combine(f.DataRoot, "trash", "history")).FullName;
        for (var i = 0; i < 1100; i++) await File.WriteAllTextAsync(Path.Combine(history, document.Id + "." + i + ".txt"), "history");
        await File.WriteAllTextAsync(Path.Combine(history, "unrelated.txt"), "keep");
        await f.Folders.PurgeAsync("folder", Admin);
        Assert.Single(Directory.GetFiles(history));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(history, "unrelated.txt")));
        Assert.Equal("purged", (await f.Documents.GetAsync(document.Id))!.Status);
    }

    [Theory]
    [InlineData(MutationCrashPointNames.AfterSourceRemoval)]
    [InlineData(CrashPointNames.AfterIndexCommit)]
    public async Task InterruptedPurgeRecoversItsDurableRequestAfterCredentialRevocation(string point)
    {
        await using var f = await Fixture.Create(); var document = await f.Import("crash.txt", "recover-private");
        f.Crash.Point = point;
        await Assert.ThrowsAnyAsync<Exception>(async () => await f.Folders.PurgeAsync("folder", Admin));
        Assert.Equal(1L, await f.Scalar("SELECT COUNT(*) FROM folder_source_purges WHERE completed_at IS NULL"));
        f.Crash.Point = null;
        await f.Write("UPDATE credentials SET generation=2 WHERE id='purge-admin'");
        Assert.Equal(1, await f.Folders.RecoverPurgesAsync());
        Assert.Equal("purged", (await f.Documents.GetAsync(document.Id))!.Status);
        Assert.Equal(1L, await f.Scalar("SELECT COUNT(*) FROM revisions WHERE document_id=@id AND cause='purge'", new { id = document.Id }));
        Assert.Equal(0L, await f.Scalar("SELECT COUNT(*) FROM payloads WHERE instr(CAST(bytes AS TEXT),'recover-private')>0"));
    }

    [Fact]
    public async Task UnsafeOwnedCopyDefersOneDocumentAndStaleWorkerCannotRecreateIt()
    {
        await using var f = await Fixture.Create(); var first = await f.Import("unsafe.txt", "unsafe-private"); var other = await f.Import("normal.txt", "normal-private");
        var link = Path.Combine(Path.GetDirectoryName(first.Path)!, first.Id + ".link.txt"); File.CreateSymbolicLink(link, Path.Combine(f.External, "unsafe.txt"));
        await Assert.ThrowsAsync<IngestionConflictException>(async () => await f.Folders.PurgeAsync("folder", Admin));
        Assert.Equal("purged", (await f.Documents.GetAsync(other.Id))!.Status);
        var current = (await f.Documents.GetAsync(first.Id))!;
        Assert.Equal(PublicationOutcome.Superseded, await f.Publications.PublishAsync(new(first.Id, "folder", current.Revision, "{}", "extract", "Late worker", "late", "folder") { Projection = TextProcessor.Process(first.Id, "Late worker", "late private") }));
        await Assert.ThrowsAsync<IngestionConflictException>(async () => await f.Import("late.txt", "not admitted"));
        File.Delete(link);
        Assert.Equal(1, await f.Folders.RecoverPurgesAsync());
        Assert.Equal("unsafe-private", await File.ReadAllTextAsync(Path.Combine(f.External, "unsafe.txt")));
    }

    [Fact]
    public async Task PurgeSettlesAnInterruptedEarlierDocumentMutation()
    {
        await using var f = await Fixture.Create(); var document = await f.Import("interrupted.txt", "interrupted-private");
        f.Crash.Point = CrashPointNames.AfterApply;
        await Assert.ThrowsAsync<IOException>(async () => await f.Documents.MutateAsync(document.Id, document.Revision, "delete", JsonSerializer.SerializeToElement(new { }), Admin));
        f.Crash.Point = null;
        await f.Folders.PurgeAsync("folder", Admin);
        Assert.Equal("purged", (await f.Documents.GetAsync(document.Id))!.Status);
        Assert.Equal(0L, await f.Scalar("SELECT COUNT(*) FROM mutations WHERE status IN ('prepared','applied')"));
        Assert.Equal(0L, await f.Scalar("SELECT COUNT(*) FROM payloads WHERE instr(CAST(bytes AS TEXT),'interrupted-private')>0"));
    }

    [Fact]
    public async Task CalendarContainerMembersAndCoverageArePurgedWithTheirFolder()
    {
        await using var f = await Fixture.Create(); var document = await f.Import("calendar.ics", "calendar-private-original");
        var props = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"uid\":\"one\",\"starts_at\":\"2026-10-25T09:00:00Z\",\"ends_at\":\"2026-10-25T10:00:00Z\",\"all_day\":false,\"status\":\"confirmed\"}")!;
        var calendar = new CalendarSnapshot(new("2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z", "UTC"),
            [new("one", "", "Private event", "private calendar event", props)],
            [new("one", "", "2026-10-25T09:00:00Z", "2026-10-25T10:00:00Z", "confirmed")]);
        var generation = (string)(await f.Scalar("SELECT generation_json FROM publication_fences WHERE document_id=@id",new {id=document.Id}))!;
        Assert.Equal(PublicationOutcome.Published, await f.Publications.PublishCalendarAsync(new(document.Id,document.Revision,generation),calendar));
        await f.Folders.PurgeAsync("folder",Admin);
        Assert.Equal(2L,await f.Scalar("SELECT COUNT(*) FROM documents WHERE source_id='folder' AND status='purged'"));
        Assert.Equal(0L,await f.Scalar("SELECT COUNT(*) FROM calendar_coverage"));
        Assert.Equal(0L,await f.Scalar("SELECT COUNT(*) FROM payloads WHERE instr(CAST(bytes AS TEXT),'private calendar event')>0"));
        Assert.Equal("calendar-private-original",await File.ReadAllTextAsync(Path.Combine(f.External,"calendar.ics")));
    }

    [Fact]
    public async Task PurgeWaitsForAnAdmittedImportThenRemovesItsRetainedBytes()
    {
        await using var f = await Fixture.Create(); f.Crash.BlockPoint = CrashPointNames.AfterStatePublishing;
        var admission = f.Import("overlap.txt", "overlap-private");
        await f.Crash.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var purge = f.Folders.PurgeAsync("folder", Admin).AsTask();
        try { await Task.Delay(50); Assert.False(purge.IsCompleted); }
        finally { f.Crash.Continue.TrySetResult(); }
        var document = await admission; await purge;
        Assert.Equal("purged", (await f.Documents.GetAsync(document.Id))!.Status);
        Assert.Equal(0L, await f.Scalar("SELECT COUNT(*) FROM payloads WHERE instr(CAST(bytes AS TEXT),'overlap-private')>0"));
        Assert.Equal("overlap-private", await File.ReadAllTextAsync(Path.Combine(f.External, "overlap.txt")));
    }

    private sealed class Crash : ICrashPoints
    {
        internal string? Point, BlockPoint;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask HitAsync(string point, CancellationToken cancellationToken = default)
        {
            if (point == Point) throw new IOException("Injected interruption");
            if (point != BlockPoint) return;
            BlockPoint = null; Entered.TrySetResult(); await Continue.Task.WaitAsync(cancellationToken);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MigrationTestDataRoot root = new();
        internal string DataRoot => root.Path;
        internal string External { get; } = Path.Combine(Path.GetTempPath(), "sb-folder-purge-" + Guid.NewGuid().ToString("N"));
        internal MigrationTestStores Stores { get; }
        internal Crash Crash { get; } = new();
        internal MutationJournal Journal { get; }
        internal ManagedDocumentFiles Files { get; }
        internal PublicationCoordinator Publications { get; }
        internal SqliteDocumentRepository Documents { get; }
        internal SqliteFolderDocumentRepository Folders { get; }
        internal SourceRecord Source { get; }
        private Fixture()
        {
            Stores = new(root.Path); var options = Options.Create(new SecondBrainOptions { DataRoot = root.Path });
            Journal = new(Stores.State, root.Path, Crash); Files = new(root.Path, Journal); Publications = new(Stores.State, Stores.Index, Crash);
            Documents = new(Stores.State, Stores.Index, Publications, Files, options);
            Folders = new(Stores.State, Publications, Documents, Files, options);
            Source = new("folder", "folder", "Folder", new(External, ["**"], [], true, "index", false, null, new Dictionary<string,string>(), "poll", 60), "active", null, null, DateTimeOffset.UtcNow);
        }
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture(); Directory.CreateDirectory(f.External); await f.Stores.Runner.MigrateAsync();
            await f.Write("INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
            await new SqliteSourceRepository(f.Stores.State).AddAsync(f.Source);
            await new AuthRepository(f.Stores.State, TimeProvider.System).AddAsync(new CredentialRecord { Id=Admin.CredentialId, Name="admin",Kind="session",Generation=1,AccountEpoch=1,Scopes="read,write,admin",Verifier="fixture",Kid="fixture",CreatedAt=DateTimeOffset.UtcNow.ToString("O"),SteppedUpAt=DateTimeOffset.UtcNow.ToString("O") });
            return f;
        }
        internal async Task<DocumentRecord> Import(string name, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content); await File.WriteAllBytesAsync(Path.Combine(External,name),bytes);
            var candidate = new FolderImportCandidate(new(name, bytes.Length, DateTimeOffset.UtcNow, Convert.ToHexStringLower(SHA256.HashData(bytes))),null,0);
            return (await Folders.AdmitSourceAsync(Source,candidate,bytes,candidate.Entry.Sha256!))!.Document;
        }
        internal Task Write(string sql) => Stores.State.QueueWriteAsync(async(c,t,ct)=> {await c.ExecuteAsync(new CommandDefinition(sql,new{now=DateTimeOffset.UtcNow.ToString("O")},t,cancellationToken:ct));return 0;}).AsTask();
        internal async Task<object?> Scalar(string sql, object? args=null) {await using var read=await Stores.State.OpenReadConnectionAsync();return await read.Connection.ExecuteScalarAsync(sql,args);}
        public async ValueTask DisposeAsync() {Files.Dispose();Journal.Dispose();await Stores.DisposeAsync();root.Dispose();Directory.Delete(External,true);}
    }
}
