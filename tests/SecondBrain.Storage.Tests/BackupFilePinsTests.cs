using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Ingestion;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Backup;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class BackupFilePinsTests
{
    private static readonly IngestionAuthority Admin = new("backup-admin",1,1,"backup test","session");

    [Fact]
    public async Task CaptureDefersFileCopyAndPreservesTheOriginalVersionBeforeAnEdit()
    {
        await using var f=await Fixture.Create(); var note=await f.Note("first version");
        await using var pins=await f.Capture();
        Assert.All(await Rows(pins), row=>Assert.Equal("pending",row.Status));
        var snapshot=await ReadSnapshot(pins.StateSnapshotPath,note.Id);
        Assert.Equal(note.Revision,snapshot.Revision);
        await f.Edit(note.Id,"second version");
        var saved=Assert.Single(await Rows(pins));Assert.Equal("copied",saved.Status);
        Assert.Equal(snapshot.Hash,saved.Sha256);
        Assert.Contains("first version",await File.ReadAllTextAsync(saved.CopyPath!));
        Assert.Contains("second version",await File.ReadAllTextAsync(note.Path!));
        await pins.CopyAllAsync(); Assert.Single(await Rows(pins));
    }

    [Fact]
    public async Task ADataRootWithATrailingSeparatorStillPreservesEditedFiles()
    {
        await using var f = await Fixture.Create(); var note = await f.Note("before trailing separator");
        await using var pins = await BackupFilePins.CaptureAsync(f.Journal, f.Stores.State, f.DataRoot + Path.DirectorySeparatorChar,
            [new("vault", Path.Combine(f.DataRoot, "vault"))]);
        await f.Edit(note.Id, "after trailing separator");
        var file = Assert.Single(await Rows(pins)); Assert.Equal("copied", file.Status);
        Assert.Contains("before trailing separator", await File.ReadAllTextAsync(file.CopyPath!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrashAndPurgeCannotRemoveTheCapturedVersion(bool purge)
    {
        await using var f=await Fixture.Create();var note=await f.Note("retained body");
        if(purge)await f.Mutate(note.Id,"delete");
        await using var pins=await f.Capture();var snapshot=await ReadSnapshot(pins.StateSnapshotPath,note.Id);
        await f.Mutate(note.Id,purge?"purge":"delete");await pins.CopyAllAsync();
        var file=Assert.Single(await Rows(pins));Assert.Equal("copied",file.Status);Assert.Equal(snapshot.Hash,file.Sha256);
        Assert.Contains("retained body",await File.ReadAllTextAsync(file.CopyPath!));
        Assert.Equal(purge?"purged":"deleted",(await f.Documents.GetAsync(note.Id))!.Status);
    }

    [Fact]
    public async Task SustainedEditsLeaveASnapshotAndManifestWithMatchingFileHashes()
    {
        await using var f=await Fixture.Create();var first=await f.Note("initial one");var second=await f.Note("initial two");
        await using var pins=await f.Capture();
        var writer=Task.Run(async()=>{for(var i=0;i<24;i++){await f.Edit(first.Id,"first edit "+i);await f.Edit(second.Id,"second edit "+i);}});
        await pins.CopyAllAsync();await writer;
        var rows=await Rows(pins);Assert.Equal(2,rows.Count);Assert.All(rows,row=>Assert.Equal("copied",row.Status));
        foreach(var note in new[]{first,second})
        {
            var row=Assert.Single(rows,item=>Path.GetFileName(item.RelativePath)==Path.GetFileName(note.Path));
            var saved=await ReadSnapshot(pins.StateSnapshotPath,note.Id);Assert.Equal(saved.Hash,row.Sha256);
            Assert.Equal(row.Sha256,Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(row.CopyPath!))));
            Assert.True((await f.Documents.GetAsync(note.Id))!.Revision>saved.Revision);
        }
    }

    [Fact]
    public async Task CaptureWaitsForAnAppliedFileToCommitItsAuthoritativeRevision()
    {
        var pause = new PauseAfterApply();
        await using var f = await Fixture.Create(pause); var note = await f.Note("initial");
        pause.Enabled = true;
        var edit = f.Edit(note.Id, "committed replacement");
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var capture = f.Capture();
        try { Assert.False(capture.IsCompleted); }
        finally { pause.Continue.TrySetResult(); }
        await edit;
        await using var pins = await capture;
        await pins.CopyAllAsync();
        var file = Assert.Single(await Rows(pins)); var state = await ReadSnapshot(pins.StateSnapshotPath, note.Id);
        Assert.Equal(state.Hash, file.Sha256); Assert.Equal(2, state.Revision);
        Assert.Contains("committed replacement", await File.ReadAllTextAsync(file.CopyPath!));
    }

    [Fact]
    public async Task UnjournaledExternalChangeMakesTheBackupPartialInsteadOfChangingItsVersion()
    {
        await using var f=await Fixture.Create();Directory.CreateDirectory(f.External);var file=Path.Combine(f.External,"external.txt");await File.WriteAllTextAsync(file,"first");
        await using var pins=await f.Capture([new("external",f.External)]);
        await File.WriteAllTextAsync(file,"changed external original");await pins.CopyAllAsync();
        var row=Assert.Single(await Rows(pins));Assert.Equal("partial",row.Status);Assert.Null(row.CopyPath);Assert.Null(row.Sha256);Assert.NotNull(row.Error);
        Assert.Equal("changed external original",await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task LinksAreRecordedAsPartialAndNeverFollowed()
    {
        await using var f=await Fixture.Create();Directory.CreateDirectory(f.External);var source=Path.Combine(f.External,"ordinary.txt");await File.WriteAllTextAsync(source,"ordinary");
        var outside=Directory.CreateDirectory(Path.Combine(f.External,"outside")).FullName;await File.WriteAllTextAsync(Path.Combine(outside,"secret.txt"),"outside body");
        var root=Directory.CreateDirectory(Path.Combine(f.External,"root")).FullName;
        File.CreateSymbolicLink(Path.Combine(root,"linked.txt"),source);Directory.CreateSymbolicLink(Path.Combine(root,"linked-directory"),outside);
        await using var pins=await f.Capture([new("external",root)]);await pins.CopyAllAsync();
        var rows=await Rows(pins);Assert.Equal(2,rows.Count);Assert.All(rows,row=>{Assert.Equal("partial",row.Status);Assert.Equal("linked_entry",row.Error);Assert.Null(row.CopyPath);});
        Assert.DoesNotContain(rows,row=>row.RelativePath.EndsWith("secret.txt",StringComparison.Ordinal));
    }

    [Fact]
    public async Task SnapshotWorkspaceAndCopiesStayPrivateAndAreRemovedAtDisposal()
    {
        await using var f=await Fixture.Create();var note=await f.Note("private");var pins=await f.Capture();await pins.CopyAllAsync();
        var workspace=pins.WorkspacePath;
        if(!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute,File.GetUnixFileMode(workspace));
            foreach(var file in Directory.EnumerateFiles(workspace))Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(file));
        }
        await pins.DisposeAsync();Assert.False(Directory.Exists(workspace));
        await f.Edit(note.Id,"later");
        await using var next=await f.Capture();Assert.NotEqual(workspace,next.WorkspacePath);
    }

    [Fact]
    public async Task ConcurrentCaptureIsRejectedWithoutClosingTheExistingPins()
    {
        await using var f=await Fixture.Create();var note=await f.Note("before");await using var first=await f.Capture();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Capture());
        await f.Edit(note.Id,"after");var file=Assert.Single(await Rows(first));Assert.Contains("before",await File.ReadAllTextAsync(file.CopyPath!));
    }

    [Fact]
    public async Task LockedManifestForOneDataRootDoesNotBlockAnotherRootsEdits()
    {
        await using var first = await Fixture.Create(); await first.Note("first root");
        await using var second = await Fixture.Create(); var note = await second.Note("second root");
        await using var pins = await first.Capture();
        await using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = pins.ManifestPath, Pooling = false }.ToString());
        await blocker.OpenAsync(); await blocker.ExecuteAsync("BEGIN EXCLUSIVE");
        var edit = second.Edit(note.Id, "unrelated root still writes");
        try { await edit.WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { await blocker.ExecuteAsync("ROLLBACK"); await edit; }
        Assert.Contains("unrelated root still writes", await File.ReadAllTextAsync(note.Path!));
    }

    [Fact]
    public async Task CancellationCleansItsWorkspaceAndDoesNotLeaveARegisteredCapture()
    {
        await using var f=await Fixture.Create();await f.Note("before");using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>BackupFilePins.CaptureAsync(f.Journal,f.Stores.State,f.DataRoot,[new("vault",Path.Combine(f.DataRoot,"vault"))],cancel.Token));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.DataRoot,"staging"),"backup-*"));
        await using var pins=await f.Capture();Assert.Single(await Rows(pins));
    }

    [Fact]
    public async Task RejectsWorkspaceContainingAndOverlappingSourceRoots()
    {
        await using var f=await Fixture.Create();await f.Note("body");
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Capture([new("everything",f.DataRoot)]));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Capture([new("vault",Path.Combine(f.DataRoot,"vault")),new("nested",Path.Combine(f.DataRoot,"vault","notes"))]));
    }

    private static async Task<List<BackupPinnedFile>> Rows(BackupFilePins pins)
    {var result=new List<BackupPinnedFile>();await foreach(var row in pins.FilesAsync())result.Add(row);return result;}
    private static async Task<(long Revision,string Hash)> ReadSnapshot(string path,string id)
    {
        await using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());await connection.OpenAsync();
        return await connection.QuerySingleAsync<(long Revision,string Hash)>("SELECT revision,content_hash FROM documents WHERE id=@id",new{id});
    }
    private sealed class PauseAfterApply : SecondBrain.Core.Durability.ICrashPoints
    {
        internal bool Enabled;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask HitAsync(string point, CancellationToken token = default)
        {
            if (!Enabled || point != SecondBrain.Core.Durability.CrashPointNames.AfterApply) return;
            Enabled = false; Entered.TrySetResult(); await Continue.Task.WaitAsync(token);
        }
    }
    private sealed class Fixture:IAsyncDisposable
    {
        private readonly MigrationTestDataRoot root=new();internal string DataRoot=>root.Path;
        internal string External{get;}=Path.Combine(Path.GetTempPath(),"sb-backup-external-"+Guid.NewGuid().ToString("N"));
        internal MigrationTestStores Stores{get;}internal MutationJournal Journal{get;}internal ManagedDocumentFiles Files{get;}internal SqliteDocumentRepository Documents{get;}
        private Fixture(SecondBrain.Core.Durability.ICrashPoints? crash)
        {
            Stores=new(root.Path);Journal=new(Stores.State,root.Path,crash);Files=new(root.Path,Journal);var publications=new PublicationCoordinator(Stores.State,Stores.Index);
            Documents=new(Stores.State,Stores.Index,publications,Files,Options.Create(new SecondBrain.Core.Configuration.SecondBrainOptions{DataRoot=root.Path}));
        }
        internal static async Task<Fixture> Create(SecondBrain.Core.Durability.ICrashPoints? crash = null)
        {
            var f=new Fixture(crash);Directory.CreateDirectory(f.External);await f.Stores.Runner.MigrateAsync();
            await f.Stores.State.QueueWriteAsync(async(c,t,ct)=>{await c.ExecuteAsync(new CommandDefinition("INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'",transaction:t,cancellationToken:ct));return 0;});
            await new AuthRepository(f.Stores.State,TimeProvider.System).AddAsync(new CredentialRecord{Id=Admin.CredentialId,Name="backup test",Kind="session",Generation=1,AccountEpoch=1,Scopes="read,write,admin",Verifier="fixture",Kid="fixture",CreatedAt=DateTimeOffset.UtcNow.ToString("O"),SteppedUpAt=DateTimeOffset.UtcNow.ToString("O")});
            Directory.CreateDirectory(Path.Combine(f.DataRoot,"vault"));Directory.CreateDirectory(Path.Combine(f.DataRoot,"trash"));return f;
        }
        internal Task<BackupFilePins> Capture(IReadOnlyList<BackupRoot>? roots=null)=>BackupFilePins.CaptureAsync(Journal,Stores.State,DataRoot,roots??[new("vault",Path.Combine(DataRoot,"vault")),new("trash",Path.Combine(DataRoot,"trash"))]);
        internal async Task<DocumentRecord> Note(string body)=>(await Documents.CaptureAsync(new(body,body),Admin)).Document;
        internal async Task Edit(string id,string body)=>await Documents.MutateAsync(id,(await Documents.GetAsync(id))!.Revision,"update",JsonSerializer.SerializeToElement(new{content=body}),Admin);
        internal async Task Mutate(string id,string operation)=>await Documents.MutateAsync(id,(await Documents.GetAsync(id))!.Revision,operation,JsonSerializer.SerializeToElement(new{}),Admin);
        public async ValueTask DisposeAsync(){Files.Dispose();Journal.Dispose();await Stores.DisposeAsync();root.Dispose();Directory.Delete(External,true);}
    }
}
