using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Ingestion;
using SecondBrain.Server.Chat;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class M4NativeCitationQualification
{
    [Fact]
    [Trait("Category","Qualification")]
    public async Task BrowserWorkflow()
    {
        Assert.Equal("1",Environment.GetEnvironmentVariable("SECONDBRAIN_M4_NATIVE_CITATION_HARNESS"));
        var output=Environment.GetEnvironmentVariable("SECONDBRAIN_M4_NATIVE_CITATION_OUTPUT")!;Directory.CreateDirectory(output);
        var port=int.Parse(Environment.GetEnvironmentVariable("SECONDBRAIN_M4_NATIVE_CITATION_PORT")??"18331",System.Globalization.CultureInfo.InvariantCulture);
        var origin="http://127.0.0.1:"+port;
        await using var native=await NativeCitationProviderFixture.CreateAsync();
        await using var factory=NativeCitationWorkflowTests.Factory(native,true,options=> {
            options.Server.Listeners=[new(){Bind="127.0.0.1",Port=port}];options.Server.Hosts=["127.0.0.1:"+port];options.Server.Origins=[origin];
        });
        factory.UseKestrel(_=>{});factory.StartServer();var actor=await NativeCitationWorkflowTests.Actor(factory);
        var document=(await factory.Services.GetRequiredService<IDocumentRepository>().CaptureAsync(new("Orchard code name is Blue Otter. This other sentence belongs to the full captured passage.","Native citation browser fixture"),NativeCitationWorkflowTests.Authority(actor))).Document;
        var start=await factory.Services.GetRequiredService<ChatService>().StartAsync(new("Orchard code name",Mode:"ask"),actor);
        var final=await NativeCitationWorkflowTests.Finish(factory.Services.GetRequiredService<IChatRepository>(),start.Turn.Id,actor);
        Assert.Equal("span",Assert.Single(final.Answer!.Citations).Granularity);
        await File.WriteAllTextAsync(Path.Combine(output,"ready.json"),JsonSerializer.Serialize(new {origin,conversationId=final.ConversationId,turnId=final.Id,documentId=document.Id,syntheticData=true,controlledNativeProvider=true}));
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(20));while(!File.Exists(Path.Combine(output,"stop")))await Task.Delay(500,timeout.Token);
    }
}
