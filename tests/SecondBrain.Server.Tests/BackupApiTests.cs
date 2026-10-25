using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Sources;
using SecondBrain.Core.Limits;
using SecondBrain.Storage;
using Dapper;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Operations;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Tests.Support;
using SecondBrain.Storage.Backup;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class BackupApiTests
{
    private static readonly byte[] Key=System.Security.Cryptography.SHA256.HashData("backup-api-test"u8);
    [Theory]
    [InlineData("")]
    [InlineData("/v1")]
    public async Task AdminCreatesListsAndDownloadsAnEncryptedConsistentArchive(string prefix)
    {
        await using var factory=new LaneDWebFactory(configureServices:s=>s.Replace(ServiceDescriptor.Singleton<IBackupKeyProvider>(new Keys())));
        await factory.SeedAccountAsync();using var client=factory.CreatePrivateClient();client.DefaultRequestHeaders.Add("Origin","http://private.test");
        using var login=await client.PostAsJsonAsync("/auth/login",new LoginRequest("test-password"));login.EnsureSuccessStatusCode();await Csrf(client);
        var keyStatus=(await client.GetFromJsonAsync<BackupKeyStatus>(prefix+"/maintenance/backup/key"))!;Assert.True(keyStatus.Ready);
        using var before=await client.PostAsync(prefix+"/maintenance/backup",null);Assert.Equal(HttpStatusCode.Forbidden,before.StatusCode);
        using var step=await client.PostAsJsonAsync("/auth/step-up",new LoginRequest("test-password"));step.EnsureSuccessStatusCode();await Csrf(client);
        using var added=await client.PostAsJsonAsync("/documents",new CaptureRequest("Backup API content","Backup API"));added.EnsureSuccessStatusCode();
        var note=(await added.Content.ReadFromJsonAsync<CaptureResult>())!.Document;
        using var response=await client.PostAsync(prefix+"/maintenance/backup",null);Assert.Equal(HttpStatusCode.Created,response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var created=(await response.Content.ReadFromJsonAsync<BackupCreated>())!;Assert.False(created.Archive.Partial);Assert.True(created.Files>=2);
        var list=(await client.GetFromJsonAsync<BackupInfo[]>(prefix+"/maintenance/backups"))!;Assert.Contains(list,item=>item.Name==created.Archive.Name);
        using var download=await client.GetAsync(prefix+"/maintenance/backups/"+created.Archive.Name);download.EnsureSuccessStatusCode();
        Assert.True(download.Headers.CacheControl?.NoStore);
        var bytes=await download.Content.ReadAsByteArrayAsync();Assert.Equal(created.Archive.Bytes,bytes.LongLength);
        Assert.DoesNotContain("Backup API content",System.Text.Encoding.UTF8.GetString(bytes));
        var staging=Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.DataRoot,"verify-backup")).FullName;
        await using var restored=await BackupPackage.VerifyAndStageAsync(new MemoryStream(bytes),Key,staging);
        var entries=new List<BackupPackageEntry>();await foreach(var entry in restored.FilesAsync())entries.Add(entry);
        var item=Assert.Single(entries,entry=>entry.DocumentId==note.Id);Assert.Contains("Backup API content",await File.ReadAllTextAsync(Path.Combine(restored.ContentPath,item.Root,item.Path)));
        using var traversal=await client.GetAsync(prefix+"/maintenance/backups/..%2fstate.db");Assert.NotEqual(HttpStatusCode.OK,traversal.StatusCode);
    }
    [Fact]
    public async Task MissingKeyAndNonadminRequestsDoNotCreateArchives()
    {
        await using var factory=new LaneDWebFactory(configureServices:s=>s.Replace(ServiceDescriptor.Singleton<IBackupKeyProvider>(new Missing())));
        var key=await factory.CreateKeyAsync(new HashSet<Scope>{Scope.Read});using var client=factory.CreatePrivateClient(false);client.DefaultRequestHeaders.Authorization=new("Bearer",key.Plaintext);
        using var denied=await client.PostAsync("/maintenance/backup",null);Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        var admin=await factory.CreateKeyAsync(new HashSet<Scope>{Scope.Admin});client.DefaultRequestHeaders.Authorization=new("Bearer",admin.Plaintext);
        var status=(await client.GetFromJsonAsync<BackupKeyStatus>("/maintenance/backup/key"))!;Assert.False(status.Ready);
        Assert.False(Directory.Exists(Path.Combine(factory.Options.CurrentValue.DataRoot,"backups")));
    }
    [Fact]
    public async Task BackupAutomaticallyIncludesRegisteredIndexSourceFolders()
    {
        await using var factory=new LaneDWebFactory(configureServices:s=>s.Replace(ServiceDescriptor.Singleton<IBackupKeyProvider>(new Keys())));
        var folder=Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0],"backup-source")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder,"external.txt"),"External backup contents");
        await factory.Services.GetRequiredService<ISourceRepository>().AddAsync(new("external-backup","folder","External",new(folder,["**"],[],true,"index",false,null,new Dictionary<string,string>(),"poll",3600),"disabled",null,null,DateTimeOffset.UtcNow));
        using var client=await Admin(factory);
        using var made=await client.PostAsync("/maintenance/backup",null);made.EnsureSuccessStatusCode();var backup=(await made.Content.ReadFromJsonAsync<BackupCreated>())!;
        var bytes=await client.GetByteArrayAsync("/maintenance/backups/"+backup.Archive.Name);
        var staging=Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.DataRoot,"check-source-backup")).FullName;
        await using var verified=await BackupPackage.VerifyAndStageAsync(new MemoryStream(bytes),Key,staging);
        var external=Assert.Single(verified.Metadata.Roots,item=>item.Kind=="external");Assert.Equal(folder,external.SourcePath);
        Assert.Equal("External backup contents",await File.ReadAllTextAsync(Path.Combine(verified.ContentPath,external.Name,"external.txt")));
    }

    [Fact]
    public async Task CapacityFailureOccursBeforeCreatingAnArchive()
    {
        var disk=new Capacity();
        await using var factory=new LaneDWebFactory(configureServices:s=>{s.Replace(ServiceDescriptor.Singleton<IBackupKeyProvider>(new Keys()));s.Replace(ServiceDescriptor.Singleton<IDiskCapacity>(disk));});
        using var client=await Admin(factory);disk.Available=0;
        using var result=await client.PostAsync("/maintenance/backup",null);Assert.Equal(HttpStatusCode.ServiceUnavailable,result.StatusCode);
        Assert.Contains("backup_capacity",await result.Content.ReadAsStringAsync());Assert.False(Directory.Exists(Path.Combine(factory.Options.CurrentValue.DataRoot,"backups")));
        Assert.Equal(factory.Options.CurrentValue.DataRoot,disk.LastPath);
    }

    [Fact]
    public async Task RevocationAfterKeyReadCannotPublishAnArchive()
    {
        await using var factory=new LaneDWebFactory(configureServices:s=>s.Replace(ServiceDescriptor.Singleton<IBackupKeyProvider>(p=>new RevokingKeys(p.GetRequiredService<IStateStore>()))));
        using var client=await Admin(factory);
        using var result=await client.PostAsync("/maintenance/backup",null);Assert.Equal(HttpStatusCode.Forbidden,result.StatusCode);
        var folder=Path.Combine(factory.Options.CurrentValue.DataRoot,"backups");Assert.Empty(Directory.Exists(folder)?Directory.GetFiles(folder):[]);
    }

    private static async Task<HttpClient> Admin(LaneDWebFactory factory)
    {
        await factory.SeedAccountAsync();var client=factory.CreatePrivateClient();client.DefaultRequestHeaders.Add("Origin","http://private.test");
        using var login=await client.PostAsJsonAsync("/auth/login",new LoginRequest("test-password"));login.EnsureSuccessStatusCode();await Csrf(client);
        using var step=await client.PostAsJsonAsync("/auth/step-up",new LoginRequest("test-password"));step.EnsureSuccessStatusCode();await Csrf(client);return client;
    }
    private sealed class Capacity:IDiskCapacity
    {internal long Available=1L<<40;internal string? LastPath;public long? AvailableBytes(string path){LastPath=path;return Available;}}
    private sealed class RevokingKeys(IStateStore state):IBackupKeyProvider
    {
        public async ValueTask<BackupKey> ReadAsync(CancellationToken token=default)
        {await state.QueueWriteAsync(async(c,t,ct)=>{await c.ExecuteAsync(new CommandDefinition("UPDATE credentials SET generation=generation+1 WHERE kind='session'",transaction:t,cancellationToken:ct));return 0;},token);return new(Key);}
    }
    private static async Task Csrf(HttpClient client){var csrf=(await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;client.DefaultRequestHeaders.Remove(csrf.HeaderName);client.DefaultRequestHeaders.Add(csrf.HeaderName,csrf.Token);}
    private sealed class Keys:IBackupKeyProvider{public ValueTask<BackupKey> ReadAsync(CancellationToken token=default)=>ValueTask.FromResult(new BackupKey(Key));}
    private sealed class Missing:IBackupKeyProvider{public ValueTask<BackupKey> ReadAsync(CancellationToken token=default)=>throw new BackupException("backup_key_unavailable");}
}
