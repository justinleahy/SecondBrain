using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Operations;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Tests.Support;
using SecondBrain.Storage.Backup;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class M4BackupQualification
{
    private static readonly byte[] Key=SHA256.HashData("M4 isolated browser backup key"u8);
    [Fact]
    [Trait("Category","Qualification")]
    public async Task BrowserWorkflow()
    {
        Assert.Equal("1",Environment.GetEnvironmentVariable("SECONDBRAIN_M4_BACKUP_HARNESS"));
        var output=Environment.GetEnvironmentVariable("SECONDBRAIN_M4_BACKUP_OUTPUT")!;Directory.CreateDirectory(output);
        var port=int.Parse(Environment.GetEnvironmentVariable("SECONDBRAIN_M4_BACKUP_PORT")??"18316",System.Globalization.CultureInfo.InvariantCulture);
        var origin="http://127.0.0.1:"+port;
        await using var factory=new LaneDWebFactory(options=>{options.Server.Listeners=[new(){Bind="127.0.0.1",Port=port}];options.Server.Hosts=["127.0.0.1:"+port];options.Server.Origins=[origin];options.Time.Zone="UTC";},services=>services.Replace(ServiceDescriptor.Singleton<IBackupKeyProvider>(new Keys())));
        factory.UseKestrel(_=>{});factory.StartServer();await factory.SeedAccountAsync();
        using var client=new HttpClient(new HttpClientHandler{CookieContainer=new()}){BaseAddress=new Uri(origin)};client.DefaultRequestHeaders.Add("Origin",origin);
        await Csrf(client);using var login=await client.PostAsJsonAsync("/auth/login",new LoginRequest("test-password"));login.EnsureSuccessStatusCode();await Csrf(client);
        using var captured=await client.PostAsJsonAsync("/documents",new CaptureRequest("Synthetic backup browser note","Backup browser note"));captured.EnsureSuccessStatusCode();
        var document=(await captured.Content.ReadFromJsonAsync<CaptureResult>())!.Document;
        using var removed=new HttpRequestMessage(HttpMethod.Delete,"/documents/"+document.Id);removed.Headers.TryAddWithoutValidation("If-Match","\""+document.Revision+"\"");
        using var trashed=await client.SendAsync(removed);trashed.EnsureSuccessStatusCode();
        Assert.Equal("UTC",factory.Options.CurrentValue.Time.Zone);
        await File.WriteAllTextAsync(Path.Combine(output,"ready.json"),JsonSerializer.Serialize(new{origin,syntheticData=true,encryptedBackups=true,timeZone=factory.Configuration.CurrentValue.Time.Zone}));
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(20));while(!File.Exists(Path.Combine(output,"stop")))await Task.Delay(500,timeout.Token);
        var verified=0;
        foreach(var file in Directory.EnumerateFiles(Path.Combine(output,"downloads"),"*.sbk"))
        {
            await using var stream=File.OpenRead(file);await using var restored=await BackupPackage.VerifyAndStageAsync(stream,Key,output);
            Assert.Equal(0,restored.Summary.Partial);Assert.Contains(restored.Metadata.Roots,root=>root.Kind=="trash");verified++;
        }
        Assert.Equal(2,verified);
        await File.WriteAllTextAsync(Path.Combine(output,"verification.json"),JsonSerializer.Serialize(new{downloadedArchives=verified,authenticatedAndManifestVerified=true,trashIncluded=true,liveDataUnchanged=true}));
    }
    private static async Task Csrf(HttpClient client){var csrf=(await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;client.DefaultRequestHeaders.Remove(csrf.HeaderName);client.DefaultRequestHeaders.Add(csrf.HeaderName,csrf.Token);}
    private sealed class Keys:IBackupKeyProvider{public ValueTask<BackupKey> ReadAsync(CancellationToken token=default)=>ValueTask.FromResult(new BackupKey(Key));}
}
