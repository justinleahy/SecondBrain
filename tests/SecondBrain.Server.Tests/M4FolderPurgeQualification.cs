using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Infrastructure.Extraction;
using SecondBrain.Server.Ingestion;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Sources;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class M4FolderPurgeQualification
{
    [Fact]
    [Trait("Category","Qualification")]
    public async Task BrowserWorkflow()
    {
        Assert.Equal("1",Environment.GetEnvironmentVariable("SECONDBRAIN_M4_FOLDER_PURGE_HARNESS"));
        var output=Environment.GetEnvironmentVariable("SECONDBRAIN_M4_FOLDER_PURGE_OUTPUT")!;Directory.CreateDirectory(output);
        var port=int.Parse(Environment.GetEnvironmentVariable("SECONDBRAIN_M4_FOLDER_PURGE_PORT")??"18314",System.Globalization.CultureInfo.InvariantCulture);
        var origin="http://127.0.0.1:"+port;
        await using var factory=new LaneDWebFactory(options=>
        {
            options.Server.Listeners=[new(){Bind="127.0.0.1",Port=port}];options.Server.Hosts=["127.0.0.1:"+port];options.Server.Origins=[origin];
        },services=>services.Replace(ServiceDescriptor.Singleton<IExtractorClient>(provider=>new DescriptorExtractorClient(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.Extractor.SocketPath))));
        factory.UseKestrel(_=>{});factory.StartServer();await factory.SeedAccountAsync();
        await factory.Services.GetRequiredService<FolderProcessingOrchestrator>().StopAsync(default);
        foreach(var width in new[]{1280,390})
        {
            var path=Directory.CreateDirectory(Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0],"purge-"+width)).FullName;
            var source=new SourceRecord(Ulid.NewUlid().ToString(),"folder","Folder purge "+width,new(path,["**"],[],true,"index",false,null,new Dictionary<string,string>(),"poll",86400),"active",null,null,DateTimeOffset.UtcNow);
            await factory.Services.GetRequiredService<ISourceRepository>().AddAsync(source);
            var bytes=Encoding.UTF8.GetBytes("Synthetic folder content "+width);await File.WriteAllBytesAsync(Path.Combine(path,"sample.txt"),bytes);
            var result=(await factory.Services.GetRequiredService<IFolderDocumentRepository>().AdmitSourceAsync(source,new(new("sample.txt",bytes.Length,DateTimeOffset.UtcNow,null),null,0),bytes,Convert.ToHexStringLower(SHA256.HashData(bytes))))!;
            result=await factory.Services.GetRequiredService<FileProcessingOrchestrator>().WaitAsync(result,15,default);Assert.Contains(result.Document.Status, new[]{"indexed","indexed_partial"});
            Assert.Contains("Synthetic folder content", (await factory.Services.GetRequiredService<IDocumentRepository>().GetAsync(result.Document.Id,true))!.Text);
        }
        await File.WriteAllTextAsync(Path.Combine(output,"ready.json"),JsonSerializer.Serialize(new{origin,syntheticData=true,realExtractor=true}));
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(20));while(!File.Exists(Path.Combine(output,"stop")))await Task.Delay(500,timeout.Token);
        foreach(var width in new[]{1280,390})Assert.True(File.Exists(Path.Combine(factory.Options.CurrentValue.Sources.AllowedRoots[0],"purge-"+width,"sample.txt")));
    }
}
