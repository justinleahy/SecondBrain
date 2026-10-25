using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class M4OllamaQualification
{
    [Fact]
    [Trait("Category","Qualification")]
    public async Task ExistingLocalOllamaRunsEnrichmentAndRecordsUsage()
    {
        Assert.Equal("1",Environment.GetEnvironmentVariable("SECONDBRAIN_M4_OLLAMA_LIVE"));
        var output=Environment.GetEnvironmentVariable("SECONDBRAIN_M4_OLLAMA_OUTPUT")!;
        await using var factory=new LaneDWebFactory(options=> {
            options.Enrichment.Enabled=true;
            options.Providers["ollama-live"]=new(){Kind="ollama",Endpoint="http://127.0.0.1:11434/",Trusted=true};
            options.Privacy.TrustedServices.Add("http://127.0.0.1:11434");
            options.Models.Enrich=new(){Provider="ollama-live",Model="qwen2.5:1.5b",Capabilities=new(){StructuredOutput=true},Limits=new(){ContextTokens=8192,MaxOutputTokens=1024}};
        });
        await factory.SeedAccountAsync();
        var key=await factory.CreateKeyAsync(new HashSet<Scope>{Scope.Read,Scope.Write,Scope.Infer});
        var documents=factory.Services.GetRequiredService<IDocumentRepository>();
        var captured=await documents.CaptureAsync(new("Ada plans a community garden. She will plant carrots in April and ask the neighborhood group to share watering duties.","Garden plan"),new(key.Record.Id,key.Record.Generation,key.Record.AccountEpoch,"fixture","api_key"));
        DocumentRecord? result=null;
        for(var i=0;i<100;i++) { result=await documents.GetAsync(captured.Document.Id,true);if(result?.Enrichment is not null)break;await Task.Delay(500);}
        Assert.NotNull(result?.Enrichment); Assert.NotEmpty(result.Enrichment.Summary);
        Assert.Equal(captured.Document.Revision,result.Revision);
        await using var read=await factory.Store.OpenReadConnectionAsync();
        var calls=await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM usage WHERE provider='ollama-live' AND role='enrich' AND settled=1 AND input_tokens>0 AND output_tokens>0 AND credential_id IS NOT NULL AND job_id IS NOT NULL");
        Assert.Equal(1,calls); Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output,"live-ollama.json"),JsonSerializer.Serialize(new {provider="ollama",model="qwen2.5:1.5b",syntheticData=true,summaryCharacters=result.Enrichment.Summary.Length,tags=result.Enrichment.Tags.Length,settledAttributedCalls=calls,originalRevisionPreserved=true}));
    }
}
