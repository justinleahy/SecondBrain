using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Limits;
using SecondBrain.Core.Operations;
using SecondBrain.Core.Storage;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;

namespace SecondBrain.Storage.Backup;

public sealed partial class BackupOperations(IStateStore state, IIndexStore index, IStoreSnapshot snapshot, IMutationJournal journal,
    IBackupKeyProvider keys, IOptionsMonitor<SecondBrainOptions> options, IDiskCapacity capacity, TimeProvider clock,
    RuntimeLocations? locations = null) : IBackupOperations, IExportOperations, IMigrationBackup
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStateStore, SemaphoreSlim> Gates = new();
    private string Root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.CurrentValue.DataRoot));
    private string ArchiveDirectory => Path.Combine(Root,"backups");

    public Task<BackupCreated> CreateAsync(IngestionAuthority actor, CancellationToken token = default) => CreateCoreAsync(actor, token);

    async Task<string> IMigrationBackup.CreateAsync(int targetVersion, CancellationToken token)
    {
        if (targetVersion != Migrations.M4SchemaTransition.Version) throw new InvalidOperationException("Unsupported pre-migration backup version.");
        var created = await CreateCoreAsync(null, token);
        if (created.Archive.Partial) throw new InvalidDataException("The pre-migration backup is incomplete; repair unavailable source files before upgrading.");
        return created.Archive.Name;
    }

    private Task AuthorizeIfActor(IngestionAuthority? actor, CancellationToken token) =>
        actor is null ? Task.CompletedTask : AuthorizeAsync(actor, token);

    private async Task<BackupCreated> CreateCoreAsync(IngestionAuthority? actor, CancellationToken token)
    {
        await AuthorizeIfActor(actor,token);
        var gate=Gates.GetValue(state,static _=>new(1,1));
        if(!await gate.WaitAsync(0,token))throw new BackupException("backup_busy");
        try
        {
            using var key=await keys.ReadAsync(token);
            await AuthorizeIfActor(actor,token);
            var statePath=Path.Combine(Root,"state","state.db");
            var initial=File.Exists(statePath)?new FileInfo(statePath).Length:0;
            CheckCapacity(checked(initial*3));
            EnsurePrivateDirectory(ArchiveDirectory);
            if(journal is not MutationJournal mutations)throw new InvalidOperationException("Backup requires the durable mutation journal.");
            for(var attempt=0;attempt<3;attempt++)
            {
                var roots=await RootsAsync(state,token);
                await using var captured=await BackupFilePins.CaptureAsync(mutations,snapshot,Root,roots,token);
                await using(var copy=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=captured.StateSnapshotPath,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
                {
                    await copy.OpenAsync(token);var needed=await SourcePathsAsync(copy,token);
                    if(needed.Any(path=>!roots.Any(root=>root.Name.StartsWith("external",StringComparison.Ordinal)&&Within(path,root.Path))))continue;
                }
                long bytes=new FileInfo(captured.StateSnapshotPath).Length;
                await foreach(var file in captured.FilesAsync(token))bytes=checked(bytes+file.Length);
                CheckCapacity(checked(bytes*2+1024*1024));
                int indexVersion;
                await using(var read=await index.OpenReadConnectionAsync(token))
                    indexVersion=await BackupPackage.SchemaVersionAsync(read.Connection,token);
                var metadata=await BackupPackage.DescribeAsync(captured,indexVersion,token);
                await AuthorizeIfActor(actor,token);
                var archive=await BackupPackage.WriteAtomicAsync(captured,ArchiveDirectory,key.Bytes,metadata,token,ct=>AuthorizeIfActor(actor,ct));
                BackupRetentionResult rotation;
                try { rotation = await RotateCoreAsync(archive.FileName, null, token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or BackupException)
                { rotation = new(0, 0, true, 1); }
                return new(new(archive.FileName,archive.Length,metadata.CapturedAt,archive.Summary.Partial!=0),archive.Summary.Files,archive.Summary.Complete,archive.Summary.Partial) { Retention = rotation };
            }
            throw new BackupException("backup_sources_changed");
        }
        catch(OverflowException){throw new BackupException("backup_capacity");}
        finally{gate.Release();}
    }

    public async Task<IReadOnlyList<BackupInfo>> ListAsync(IngestionAuthority actor,CancellationToken token=default)
    {
        await AuthorizeAsync(actor,token,sensitive:false);
        if(!Directory.Exists(ArchiveDirectory))return [];
        RequireDirectory(ArchiveDirectory);
        var newest=new SortedSet<BackupInfo>(Comparer<BackupInfo>.Create((a,b)=>
        {var date=a.CreatedAt.CompareTo(b.CreatedAt);return date!=0?date:StringComparer.Ordinal.Compare(a.Name,b.Name);}));
        foreach(var path in Directory.EnumerateFiles(ArchiveDirectory))
        {
            token.ThrowIfCancellationRequested();var name=Path.GetFileName(path);
            if(!ArchiveName().IsMatch(name)||new FileInfo(path).LinkTarget is not null)continue;
            if(!DateTimeOffset.TryParseExact(name.Substring(7,23),"yyyyMMdd'T'HHmmssfffffff'Z'",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out var created))continue;
            newest.Add(new(name,new FileInfo(path).Length,created,name.EndsWith(".partial.sbk",StringComparison.Ordinal)));
            if(newest.Count>128)newest.Remove(newest.Min!);
        }
        return newest.Reverse().ToArray();
    }
    public async Task<Stream> OpenAsync(string name,IngestionAuthority actor,CancellationToken token=default)
    {
        await AuthorizeAsync(actor,token);
        if(!ArchiveName().IsMatch(name))throw new BackupException("backup_not_found");
        RequireDirectory(ArchiveDirectory);
        try
        {
            using var parent=new ManagedMutationFiles(Root).Open(Path.Combine(ArchiveDirectory,name),"backup-download");
            return parent.OpenSnapshotRead().Stream;
        }
        catch(IOException){throw new BackupException("backup_not_found");}
    }
    public async Task<BackupKeyStatus> KeyStatusAsync(IngestionAuthority actor,CancellationToken token=default)
    {
        await AuthorizeAsync(actor,token,sensitive:false);
        try{using var key=await keys.ReadAsync(token);return new(true,"ready");}
        catch(BackupException error){return new(false,error.Code);}
    }
    private async Task AuthorizeAsync(IngestionAuthority actor,CancellationToken token,bool sensitive=true)
    {
        await state.QueueWriteAsync(async(c,t,ct)=>
        {
            await IngestionAuthorityValidator.ValidateAsync(c,t,actor,Scope.Admin,clock.GetUtcNow(),
                stepUpWindow:sensitive&&actor.Kind=="session"?TimeSpan.FromMinutes(options.CurrentValue.Auth.StepUpMinutes):null,cancellationToken:ct);
            return 0;
        },token);
    }
    private void CheckCapacity(long bytes)
    {
        var free=capacity.AvailableBytes(Root);
        var reserve=(long)Math.Clamp(options.CurrentValue.Limits.Global.DiskLowWaterGb*1_000_000_000m,0,long.MaxValue);
        if(free is null||free<bytes||free-bytes<reserve)throw new BackupException("backup_capacity");
    }
    private async Task<BackupRoot[]> RootsAsync(IStateStore source,CancellationToken token, bool includeKeyring = true)
    {
        string[] external;
        await using(var read=await source.OpenReadConnectionAsync(token))external=await SourcePathsAsync(read.Connection,token);
        var security=locations??new RuntimeLocations(null,null);
        var required=external.Concat(options.CurrentValue.Backup.ExternalRoots).Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if(required.Length>128)throw new BackupException("backup_root_limit");
        var roots=new List<BackupRoot>();
        foreach(var name in includeKeyring ? new[]{"vault","trash","keyring"} : new[]{"vault","trash"}){var path=Path.Combine(Root,name);EnsurePrivateDirectory(path);roots.Add(new(name,path));}
        var ordinal=0;
        foreach(var path in required)
        {
            if(!Directory.Exists(path)||new DirectoryInfo(path).LinkTarget is not null)throw new BackupException("backup_external_root_unavailable");
            if(Within(path,Root)||Within(Root,path)||Within(security.SecretsDirectory,path)||Within(path,security.SecretsDirectory)
                ||Within(security.ConfigPath,path)||Within(options.CurrentValue.Backup.KeyFile,path))throw new BackupException("backup_root_forbidden");
            if(!options.CurrentValue.Sources.AllowedRoots.Any(root=>Within(path,root)))throw new BackupException("backup_root_forbidden");
            if(required.Any(other=>other!=path&&Within(path,other)))continue;
            roots.Add(new("external/"+(ordinal++).ToString("D4",CultureInfo.InvariantCulture),path));
        }
        if (roots.Count > 128) throw new BackupException("backup_root_limit");
        return roots.ToArray();
    }
    private static async Task<string[]> SourcePathsAsync(System.Data.Common.DbConnection source,CancellationToken token)=>
        (await source.QueryAsync<string>(Sql("SELECT DISTINCT json_extract(config_json,'$.path') FROM sources WHERE kind='folder' AND id<>'inbox' AND json_extract(config_json,'$.mode')='index' LIMIT 129",null,token))).Where(path=>!string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).ToArray();
    private static bool Within(string path,string root)
    {path=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));return path==root||path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar)?root:root+Path.DirectorySeparatorChar,StringComparison.Ordinal);}
    private static void EnsurePrivateDirectory(string path)
    {
        if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        Directory.CreateDirectory(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);RequireDirectory(path);
    }
    private static void RequireDirectory(string path)
    {
        if(!Directory.Exists(path)||new DirectoryInfo(path).LinkTarget is not null)throw new BackupException("backup_directory_unavailable");
        if(!OperatingSystem.IsWindows()&&File.GetUnixFileMode(path)!=(UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute))throw new BackupException("backup_directory_permissions");
    }
    [GeneratedRegex("^backup-[0-9]{8}T[0-9]{13}Z-[a-f0-9]{32}(?:\\.partial)?\\.sbk$",RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveName();
    private static CommandDefinition Sql(string text,object? args,CancellationToken token)=>new(text,args,cancellationToken:token);
}
