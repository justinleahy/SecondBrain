using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Sources;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class FolderSourcePurgeApiTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/v1")]
    public async Task PurgeRequiresSessionAndRecentPasswordAndPreservesSourceFiles(string prefix)
    {
        await using var factory = new LaneDWebFactory();
        await factory.Services.GetRequiredService<FolderProcessingOrchestrator>().StopAsync(default);
        var path = Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0], "purge-api")).FullName;
        var source = new SourceRecord(Ulid.NewUlid().ToString(), "folder", "Purge API", new(path, ["**"], [], true, "index", false, null, new Dictionary<string,string>(), "poll", 86400), "active", null, null, DateTimeOffset.UtcNow);
        await factory.Services.GetRequiredService<ISourceRepository>().AddAsync(source);
        var bytes = Encoding.UTF8.GetBytes("private source content"); var original = Path.Combine(path,"example.txt"); await File.WriteAllBytesAsync(original,bytes);
        var result = await factory.Services.GetRequiredService<IFolderDocumentRepository>().AdmitSourceAsync(source,new(new("example.txt",bytes.Length,DateTimeOffset.UtcNow,null),null,0),bytes,Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var key = await factory.CreateKeyAsync(new HashSet<Scope> {Scope.Admin}); using var keyed = factory.CreatePrivateClient(false); keyed.DefaultRequestHeaders.Authorization = new("Bearer",key.Plaintext);
        using var deniedKey = await keyed.DeleteAsync(prefix+"/sources/"+source.Id+"?purge=1"); Assert.Equal(HttpStatusCode.Forbidden,deniedKey.StatusCode);
        await factory.SeedAccountAsync(); using var session = factory.CreatePrivateClient(); session.DefaultRequestHeaders.Add("Origin","http://private.test");
        using var login = await session.PostAsJsonAsync("/auth/login",new LoginRequest("test-password"));login.EnsureSuccessStatusCode();await Csrf(session);
        using var deniedStep = await session.DeleteAsync(prefix+"/sources/"+source.Id+"?purge=1");Assert.Equal(HttpStatusCode.Forbidden,deniedStep.StatusCode);
        using var step = await session.PostAsJsonAsync("/auth/step-up",new LoginRequest("test-password"));step.EnsureSuccessStatusCode();await Csrf(session);
        using var invalid = await session.DeleteAsync(prefix+"/sources/"+source.Id+"?purge=yes");Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);
        Assert.True(File.Exists(result!.Document.Path));
        using var purged = await session.DeleteAsync(prefix+"/sources/"+source.Id+"?purge=1");Assert.Equal(HttpStatusCode.NoContent,purged.StatusCode);
        using var repeated = await session.DeleteAsync(prefix+"/sources/"+source.Id+"?purge=1");Assert.Equal(HttpStatusCode.NoContent,repeated.StatusCode);
        Assert.Equal("private source content",await File.ReadAllTextAsync(original));Assert.False(File.Exists(result.Document.Path));
        Assert.Equal("purged",(await factory.Services.GetRequiredService<IDocumentRepository>().GetAsync(result.Document.Id))!.Status);
        await using var read=await factory.Store.OpenReadConnectionAsync();
        Assert.Equal(1L,await read.Connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM folder_source_purges WHERE source_id=@id AND completed_at IS NOT NULL",new{id=source.Id}));
    }
    private static async Task Csrf(HttpClient client)
    {
        var csrf=(await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;client.DefaultRequestHeaders.Remove(csrf.HeaderName);client.DefaultRequestHeaders.Add(csrf.HeaderName,csrf.Token);
    }
}
