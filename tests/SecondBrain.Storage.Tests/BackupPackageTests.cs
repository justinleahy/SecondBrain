using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using SecondBrain.Core.Security;
using SecondBrain.Infrastructure.Security;
using SecondBrain.Storage.Sources;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Ingestion;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Backup;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class BackupPackageTests
{
    private static readonly byte[] Key = SHA256.HashData("synthetic backup test key"u8);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Fact]
    public async Task EncryptedRoundTripVerifiesStateTrashKeyringExternalFilesAndRevisions()
    {
        await using var f = await Fixture.Create(); var note = await f.Note("retained private note"); var trash = await f.Note("retained trash");
        await f.Change(trash.Id, "delete");
        Directory.CreateDirectory(Path.Combine(f.DataRoot,"keyring")); await File.WriteAllTextAsync(Path.Combine(f.DataRoot,"keyring","test.key"),"synthetic-key-material");
        await File.WriteAllTextAsync(Path.Combine(f.External,"source.txt"),"external original");
        await using var pins = await f.Capture(extraRoots:true);
        var metadata = await f.Describe(pins);
        await f.Change(note.Id,"update","newer note not in snapshot");
        using var archive = new MemoryStream(); var summary = await BackupPackage.WriteAsync(pins,archive,Key,metadata);
        Assert.Equal(5,summary.Complete); Assert.Equal(0,summary.Partial);
        Assert.DoesNotContain("retained private note",Encoding.UTF8.GetString(archive.ToArray()));
        archive.Position=0;
        await using var restored=await BackupPackage.VerifyAndStageAsync(archive,Key,f.Restores);
        Assert.Equal(summary,restored.Summary); Assert.Equal(metadata.InstanceId,restored.Metadata.InstanceId);
        var files=await Entries(restored);var entry=Assert.Single(files,item=>item.DocumentId==note.Id);
        Assert.Equal(note.Revision,entry.Revision);
        Assert.Contains("retained private note",await File.ReadAllTextAsync(Path.Combine(restored.ContentPath,entry.Root,entry.Path)));
        Assert.Equal("external original",await File.ReadAllTextAsync(Path.Combine(restored.ContentPath,"external/0/source.txt")));
        Assert.Equal("synthetic-key-material",await File.ReadAllTextAsync(Path.Combine(restored.ContentPath,"keyring/test.key")));
        await using var state=State(restored);await state.OpenAsync();
        Assert.Equal("deleted",await state.ExecuteScalarAsync<string>("SELECT status FROM documents WHERE id=@id",new{id=trash.Id}));
        Assert.Equal("newer note not in snapshot",(await f.Documents.GetAsync(note.Id,true))!.Text);
        if(!OperatingSystem.IsWindows())
        {
            foreach(var directory in Directory.EnumerateDirectories(restored.WorkspacePath,"*",SearchOption.AllDirectories))
                Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute,File.GetUnixFileMode(directory));
            foreach(var path in Directory.EnumerateFiles(restored.WorkspacePath,"*",SearchOption.AllDirectories))
                Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public async Task IdenticalBytesKeepTheirDistinctPathsAndUnownedCopiesStayUnowned()
    {
        await using var f = await Fixture.Create();
        var first = await f.Note("identical backup bytes");
        var second = await f.Note("identical backup bytes");
        await File.WriteAllBytesAsync(Path.Combine(f.External, "unowned.md"), await File.ReadAllBytesAsync(first.Path!));
        await using var pins = await f.Capture(extraRoots: true);
        using var archive = new MemoryStream();
        await BackupPackage.WriteAsync(pins, archive, Key, await f.Describe(pins));
        archive.Position = 0;
        await using var restored = await BackupPackage.VerifyAndStageAsync(archive, Key, f.Restores);
        var entries = await Entries(restored);
        Assert.Equal(Path.GetFileName(first.Path), Assert.Single(entries, entry => entry.DocumentId == first.Id).Path.Split('/')[^1]);
        Assert.Equal(Path.GetFileName(second.Path), Assert.Single(entries, entry => entry.DocumentId == second.Id).Path.Split('/')[^1]);
        Assert.Null(Assert.Single(entries, entry => entry.Path == "unowned.md").DocumentId);
    }

    [Fact]
    public async Task HistoricalCopyRetainsItsEarlierRevisionAfterCurrentContentChanges()
    {
        await using var f = await Fixture.Create();
        var note = await f.Note("historical backup bytes");
        var historicalName = note.Id + ".previous.md";
        await File.WriteAllBytesAsync(Path.Combine(f.External, historicalName), await File.ReadAllBytesAsync(note.Path!));
        await f.Change(note.Id, "update", "current backup bytes");
        await using var pins = await f.Capture(extraRoots: true);
        using var archive = new MemoryStream();
        await BackupPackage.WriteAsync(pins, archive, Key, await f.Describe(pins));
        archive.Position = 0;
        await using var restored = await BackupPackage.VerifyAndStageAsync(archive, Key, f.Restores);
        var entries = await Entries(restored);
        var historical = Assert.Single(entries, entry => entry.Path == historicalName);
        Assert.Equal(note.Id, historical.DocumentId);
        Assert.Equal(1, historical.Revision);
        Assert.Equal(2, Assert.Single(entries, entry => entry.Root == "vault" && entry.DocumentId == note.Id).Revision);
    }

    [Fact]
    public async Task RestoredStateAndRealKeyRingCanDecryptTheCalendarSubscriptionLink()
    {
        await using var f=await Fixture.Create();var keyDirectory=Path.Combine(f.DataRoot,"keyring");
        if(OperatingSystem.IsWindows())Directory.CreateDirectory(keyDirectory);
        else Directory.CreateDirectory(keyDirectory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var protection=DataProtectionProvider.Create(new DirectoryInfo(keyDirectory),builder=>builder.SetApplicationName(KeyRingPurposes.ApplicationName));
        using var ring=new FileKeyRing(f.DataRoot,protection);
        var secrets=new CalendarSubscriptionSecrets(ring);
        const string link="https://calendar.example.invalid/published/synthetic-backup-secret";
        var calendar=await f.Calendar(secrets.Protect(link));
        var signature=ring.Sign(ring.ActiveKid,"backup challenge"u8);
        await using var pins=await f.Capture(true);using var archive=new MemoryStream();
        await BackupPackage.WriteAsync(pins,archive,Key,await f.Describe(pins));
        Assert.DoesNotContain("synthetic-backup-secret",Encoding.UTF8.GetString(archive.ToArray()));
        archive.Position=0;await using var restored=await BackupPackage.VerifyAndStageAsync(archive,Key,f.Restores);
        var recoveredProtection=DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(restored.ContentPath,"keyring")),builder=>builder.SetApplicationName(KeyRingPurposes.ApplicationName));
        using var recoveredRing=new FileKeyRing(restored.ContentPath,recoveredProtection);
        await using var state=State(restored);await state.OpenAsync();
        var envelope=await state.ExecuteScalarAsync<string>("SELECT protected_url FROM calendar_subscriptions WHERE source_id=@calendar",new{calendar});
        Assert.Equal(link,new CalendarSubscriptionSecrets(recoveredRing).Unprotect(envelope!));
        Assert.True(recoveredRing.Verify("backup challenge"u8,signature));
    }

    [Fact]
    public async Task PartialArchiveReportsTheUncapturedFileWithoutInstallingOrInventingIt()
    {
        await using var f=await Fixture.Create();await File.WriteAllTextAsync(Path.Combine(f.External,"changed.txt"),"first");
        await using var pins=await f.Capture(true);var metadata=await f.Describe(pins);
        await File.WriteAllTextAsync(Path.Combine(f.External,"changed.txt"),"changed outside journal");
        using var archive=new MemoryStream();var written=await BackupPackage.WriteAsync(pins,archive,Key,metadata);Assert.Equal(1,written.Partial);
        archive.Position=0;await using var restored=await BackupPackage.VerifyAndStageAsync(archive,Key,f.Restores);
        var missing=Assert.Single(await Entries(restored),item=>item.Status=="partial");Assert.Equal("changed.txt",missing.Path);
        Assert.False(File.Exists(Path.Combine(restored.ContentPath,missing.Root,missing.Path)));
        Assert.Equal("changed outside journal",await File.ReadAllTextAsync(Path.Combine(f.External,"changed.txt")));
    }

    [Fact]
    public async Task AtomicWriterPublishesPrivateCompleteArchivesAndCleansFailedWrites()
    {
        await using var f=await Fixture.Create();await f.Note("atomic private content");await using var pins=await f.Capture();var metadata=await f.Describe(pins);
        var result=await BackupPackage.WriteAtomicAsync(pins,f.Archives,Key,metadata);
        Assert.True(File.Exists(result.Path));Assert.EndsWith(".sbk",result.FileName);Assert.Equal(result.Length,new FileInfo(result.Path).Length);
        Assert.Empty(Directory.GetFiles(f.Archives,"*.tmp"));
        if(!OperatingSystem.IsWindows())Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(result.Path));
        var saved=Assert.Single(await PinEntries(pins));await File.WriteAllTextAsync(saved.CopyPath!,"damaged snapshot cache");
        await Assert.ThrowsAsync<InvalidDataException>(()=>BackupPackage.WriteAtomicAsync(pins,f.Archives,Key,metadata));
        Assert.Single(Directory.GetFiles(f.Archives));
    }

    [Fact]
    public async Task TamperingAndWrongKeysLeaveNoRestoreWorkspace()
    {
        await using var f=await Fixture.Create();var valid=await f.Archive();var damaged=valid.ToArray();damaged[^1]^=1;
        await Assert.ThrowsAsync<InvalidDataException>(()=>BackupPackage.VerifyAndStageAsync(new MemoryStream(damaged),Key,f.Restores));
        await Assert.ThrowsAsync<InvalidDataException>(()=>BackupPackage.VerifyAndStageAsync(new MemoryStream(valid),new byte[32],f.Restores));
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Restores));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("nested/../../escape.txt")]
    [InlineData("/tmp/backup-escape-test.txt")]
    [InlineData("nested//empty.txt")]
    public async Task AuthenticatedTraversalHeadersCannotEscapeStaging(string path)
    {
        await using var f=await Fixture.Create();var metadata=await f.Metadata();
        var attack=await RawPackage(metadata,[(new("vault",path,1,"complete",Hash([1])),new byte[]{1})]);
        await Assert.ThrowsAsync<InvalidDataException>(()=>BackupPackage.VerifyAndStageAsync(new MemoryStream(attack),Key,f.Restores));
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Restores));
    }

    [Fact]
    public async Task AuthenticatedDuplicateFileEntriesAreRejectedAndDiscarded()
    {
        await using var f=await Fixture.Create();var metadata=await f.Metadata();var entry=new BackupPackageEntry("vault","duplicate.txt",1,"complete",Hash([1]));
        var attack=await RawPackage(metadata,[(entry,new byte[]{1}),(entry,new byte[]{1})]);
        await Assert.ThrowsAsync<InvalidDataException>(()=>BackupPackage.VerifyAndStageAsync(new MemoryStream(attack),Key,f.Restores));
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Restores));
    }

    [Fact]
    public async Task AuthenticatedPayloadHashMismatchAndMissingStateAreRejected()
    {
        await using var f=await Fixture.Create();var metadata=await f.Metadata();
        var badHash=await RawPackage(metadata,[(new("vault","bad.txt",1,"complete",Hash([1])),new byte[]{2})]);
        await Assert.ThrowsAsync<InvalidDataException>(()=>BackupPackage.VerifyAndStageAsync(new MemoryStream(badHash),Key,f.Restores));
        var absentState=await RawPackage(metadata,[]);
        await Assert.ThrowsAsync<InvalidDataException>(()=>BackupPackage.VerifyAndStageAsync(new MemoryStream(absentState),Key,f.Restores));
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Restores));
    }

    [Fact]
    public async Task StateMetadataMismatchCannotBecomeAVerifiedRestore()
    {
        await using var f=await Fixture.Create();await using var pins=await f.Capture();var metadata=await f.Describe(pins);var state=await File.ReadAllBytesAsync(pins.StateSnapshotPath);
        var attack=await RawPackage(metadata with{AccountEpoch=metadata.AccountEpoch+1},[(new("state","state.db",state.Length,"complete",Hash(state)),state)]);
        await Assert.ThrowsAsync<InvalidDataException>(()=>BackupPackage.VerifyAndStageAsync(new MemoryStream(attack),Key,f.Restores));
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Restores));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoreLimitsAreEnforcedWithoutPublishingFiles(bool byteLimit)
    {
        await using var f=await Fixture.Create();await f.Note("limit fixture");var archive=await f.Archive();
        var limit=byteLimit?new BackupRestoreLimits(100,100):new BackupRestoreLimits(10*1024*1024,1);
        await Assert.ThrowsAsync<InvalidDataException>(()=>BackupPackage.VerifyAndStageAsync(new MemoryStream(archive),Key,f.Restores,limit));
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Restores));
    }

    [Fact]
    public async Task EncryptedArchiveUnderSustainedEditsRestoresItsCapturedStateAndBytes()
    {
        await using var f=await Fixture.Create();var note=await f.Note("start body");await using var pins=await f.Capture();var metadata=await f.Describe(pins);
        var writer=Task.Run(async()=>{for(var i=0;i<32;i++)await f.Change(note.Id,"update","edit "+i);});
        using var archive=new MemoryStream();await BackupPackage.WriteAsync(pins,archive,Key,metadata);await writer;
        archive.Position=0;await using var restored=await BackupPackage.VerifyAndStageAsync(archive,Key,f.Restores);
        var file=Assert.Single(await Entries(restored),row=>row.DocumentId==note.Id);Assert.Equal(1,file.Revision);
        Assert.Equal(file.Sha256,Hash(await File.ReadAllBytesAsync(Path.Combine(restored.ContentPath,file.Root,file.Path))));
        await using var state=State(restored);await state.OpenAsync();Assert.Equal(file.Sha256,await state.ExecuteScalarAsync<string>("SELECT content_hash FROM documents WHERE id=@id",new{id=note.Id}));
        Assert.Equal("edit 31",(await f.Documents.GetAsync(note.Id,true))!.Text);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public async Task AuthenticatedOversizedOrInvalidRecordLengthsAreRejected(int length)
    {
        await using var f=await Fixture.Create();var archive=await f.Archive();
        var changed=await Repackage(archive,plain=>{BinaryPrimitives.WriteInt32BigEndian(plain.AsSpan(8,4),length);return plain;});
        await Assert.ThrowsAsync<InvalidDataException>(()=>BackupPackage.VerifyAndStageAsync(new MemoryStream(changed),Key,f.Restores));
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Restores));
    }

    [Fact]
    public async Task AuthenticatedTrailingPlaintextIsRejectedAfterTheManifest()
    {
        await using var f=await Fixture.Create();var archive=await f.Archive();
        var changed=await Repackage(archive,plain=>[..plain,0]);
        await Assert.ThrowsAsync<InvalidDataException>(()=>BackupPackage.VerifyAndStageAsync(new MemoryStream(changed),Key,f.Restores));
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Restores));
    }

    [Fact]
    public async Task SchemaFingerprintMismatchIsRejectedEvenWithMatchingFileHashes()
    {
        await using var f=await Fixture.Create();await using var pins=await f.Capture();var metadata=await f.Describe(pins);var state=await File.ReadAllBytesAsync(pins.StateSnapshotPath);
        var attack=await RawPackage(metadata with{StateSchemaSha256=new string('0',64)},[(new("state","state.db",state.Length,"complete",Hash(state)),state)]);
        await Assert.ThrowsAsync<InvalidDataException>(()=>BackupPackage.VerifyAndStageAsync(new MemoryStream(attack),Key,f.Restores));
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Restores));
    }

    [Fact]
    public async Task AtomicPartialArchivesAreLabelledAndCanBeVerifiedForRecoveryReview()
    {
        await using var f=await Fixture.Create();await File.WriteAllTextAsync(Path.Combine(f.External,"partial.txt"),"before");
        await using var pins=await f.Capture(true);var metadata=await f.Describe(pins);await File.WriteAllTextAsync(Path.Combine(f.External,"partial.txt"),"after");
        var archive=await BackupPackage.WriteAtomicAsync(pins,f.Archives,Key,metadata);Assert.EndsWith(".partial.sbk",archive.FileName);
        await using var input=File.OpenRead(archive.Path);await using var restored=await BackupPackage.VerifyAndStageAsync(input,Key,f.Restores);
        Assert.Equal(1,restored.Summary.Partial);Assert.False(File.Exists(Path.Combine(restored.ContentPath,"external/0/partial.txt")));
    }

    private static async Task<byte[]> Repackage(byte[] archive,Func<byte[],byte[]> change)
    {
        using var plain=new MemoryStream();await BackupCipher.DecryptAsync(new MemoryStream(archive),plain,Key);
        using var output=new MemoryStream();await BackupCipher.EncryptAsync(new MemoryStream(change(plain.ToArray())),output,Key);return output.ToArray();
    }

    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static SqliteConnection State(VerifiedBackupPackage package)=>new(new SqliteConnectionStringBuilder{DataSource=Path.Combine(package.ContentPath,"state","state.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
    private static async Task<List<BackupPackageEntry>> Entries(VerifiedBackupPackage package){var rows=new List<BackupPackageEntry>();await foreach(var row in package.FilesAsync())rows.Add(row);return rows;}
    private static async Task<List<BackupPinnedFile>> PinEntries(BackupFilePins pins){var rows=new List<BackupPinnedFile>();await foreach(var row in pins.FilesAsync())rows.Add(row);return rows;}
    private static async Task<byte[]> RawPackage(BackupPackageMetadata metadata,(BackupPackageEntry Entry,byte[] Body)[] files)
    {
        using var plain=new MemoryStream();plain.Write("SBKP0001"u8);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Record(object value,bool include=true){var body=JsonSerializer.SerializeToUtf8Bytes(value,value.GetType(),Json);var prefix=new byte[4];BinaryPrimitives.WriteInt32BigEndian(prefix,body.Length);plain.Write(prefix);plain.Write(body);if(include){hash.AppendData(prefix);hash.AppendData(body);}}
        Record(metadata);long complete=0,partial=0,bytes=0;
        foreach(var (entry,body) in files){Record(entry);if(entry.Status=="complete"){plain.Write(body);complete++;bytes+=body.Length;}else partial++;}
        Record(new BackupPackageSummary(files.LongLength,complete,partial,bytes,Convert.ToHexStringLower(hash.GetHashAndReset())),false);
        plain.Position=0;using var archive=new MemoryStream();await BackupCipher.EncryptAsync(plain,archive,Key);return archive.ToArray();
    }
    private sealed class Fixture:IAsyncDisposable
    {
        private readonly MigrationTestDataRoot root=new();internal string DataRoot=>root.Path;
        internal string External{get;}=Path.Combine(Path.GetTempPath(),"sb-package-external-"+Guid.NewGuid().ToString("N"));
        internal string Restores=>Path.Combine(DataRoot,"restore-test");internal string Archives=>Path.Combine(DataRoot,"archives-test");
        internal MigrationTestStores Stores{get;}internal MutationJournal Journal{get;}internal ManagedDocumentFiles Files{get;}internal SqliteDocumentRepository Documents{get;}
        private static readonly IngestionAuthority Admin=new("package-admin",1,1,"package test","session");
        private Fixture(){Stores=new(DataRoot);Journal=new(Stores.State,DataRoot);Files=new(DataRoot,Journal);Documents=new(Stores.State,Stores.Index,new PublicationCoordinator(Stores.State,Stores.Index),Files,Options.Create(new SecondBrain.Core.Configuration.SecondBrainOptions{DataRoot=DataRoot}));}
        internal static async Task<Fixture> Create()
        {
            var f=new Fixture();foreach(var path in new[]{f.External,f.Restores,f.Archives,Path.Combine(f.DataRoot,"vault"),Path.Combine(f.DataRoot,"trash")})Directory.CreateDirectory(path);
            await f.Stores.Runner.MigrateAsync();await f.Stores.State.QueueWriteAsync(async(c,t,ct)=>{await c.ExecuteAsync(new CommandDefinition("INSERT INTO meta(key,value) VALUES('account_epoch','1'),('instance_id','package-test-instance') ON CONFLICT(key) DO UPDATE SET value=excluded.value",transaction:t,cancellationToken:ct));return 0;});
            await new AuthRepository(f.Stores.State,TimeProvider.System).AddAsync(new CredentialRecord{Id=Admin.CredentialId,Name=Admin.CredentialName,Kind="session",Generation=1,AccountEpoch=1,Scopes="read,write,admin",Verifier="test",Kid="test",CreatedAt=DateTimeOffset.UtcNow.ToString("O"),SteppedUpAt=DateTimeOffset.UtcNow.ToString("O")});return f;
        }
        internal async Task<string> Calendar(string envelope) => (await new SqliteCalendarSubscriptionRepository(Stores.State,TimeProvider.System,publications:new PublicationCoordinator(Stores.State,Stores.Index),files:Files)
            .CreateAsync("Backup calendar","https://calendar.example.invalid",envelope,3600,true,Admin)).Id;
        internal async Task<DocumentRecord> Note(string body)=>(await Documents.CaptureAsync(new(body,body),Admin)).Document;
        internal async Task Change(string id,string operation,string? content=null)=>await Documents.MutateAsync(id,(await Documents.GetAsync(id))!.Revision,operation,content is null?JsonSerializer.SerializeToElement(new{}):JsonSerializer.SerializeToElement(new{content}),Admin);
        internal Task<BackupFilePins> Capture(bool extraRoots=false)
        {
            var roots=new List<BackupRoot>{new("vault",Path.Combine(DataRoot,"vault")),new("trash",Path.Combine(DataRoot,"trash"))};
            if(extraRoots){Directory.CreateDirectory(Path.Combine(DataRoot,"keyring"));roots.Add(new("keyring",Path.Combine(DataRoot,"keyring")));roots.Add(new("external/0",External));}
            return BackupFilePins.CaptureAsync(Journal,Stores.State,DataRoot,roots);
        }
        internal Task<BackupPackageMetadata> Describe(BackupFilePins pins)=>BackupPackage.DescribeAsync(pins,1,[]);
        internal async Task<BackupPackageMetadata> Metadata(){await using var pins=await Capture();return await Describe(pins);}
        internal async Task<byte[]> Archive(){await using var pins=await Capture();using var archive=new MemoryStream();await BackupPackage.WriteAsync(pins,archive,Key,await Describe(pins));return archive.ToArray();}
        public async ValueTask DisposeAsync(){Files.Dispose();Journal.Dispose();await Stores.DisposeAsync();root.Dispose();Directory.Delete(External,true);}
    }
}
