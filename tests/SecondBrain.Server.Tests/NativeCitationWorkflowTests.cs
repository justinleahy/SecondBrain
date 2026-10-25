using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Ingestion;
using SecondBrain.Server.Chat;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class NativeCitationWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeChatPersistsExactSpansAndRetainsHistoricalRevisionState(bool streaming)
    {
        await using var native=await NativeCitationProviderFixture.CreateAsync();await using var factory=Factory(native,streaming);
        var actor=await Actor(factory);var documents=factory.Services.GetRequiredService<IDocumentRepository>();
        var document=(await documents.CaptureAsync(new("Orchard code name is Blue Otter. This other sentence belongs to the full captured passage.","Orchard native citation fixture"),Authority(actor))).Document;
        var start=await factory.Services.GetRequiredService<ChatService>().StartAsync(new("Orchard code name",Mode:"ask"),actor);
        var repository=factory.Services.GetRequiredService<IChatRepository>();var final=await Finish(repository,start.Turn.Id,actor);
        Assert.Equal("complete",final.Status);var citation=Assert.Single(final.Answer!.Citations);Assert.Equal("span",citation.Granularity);
        Assert.Equal("Blue Otter",Assert.Single(citation.Spans!).Excerpt);Assert.Equal(document.Id,citation.Source.DocumentId);
        Assert.Contains("other sentence",citation.Source.Excerpt);Assert.Equal(1,citation.Source.Revision);Assert.Single(native.Documents);
        var changed=(await documents.MutateAsync(document.Id,document.Revision,"patch",JsonSerializer.SerializeToElement(new {title="Changed"}),Authority(actor))).Document;
        Assert.Equal("changed",Assert.Single((await repository.GetTurnAsync(final.Id,actor)).Answer!.Citations).State);
        await documents.MutateAsync(changed.Id,changed.Revision,"delete",JsonSerializer.SerializeToElement(new {}),Authority(actor));
        var deleted=Assert.Single((await repository.GetTurnAsync(final.Id,actor)).Answer!.Citations);
        Assert.Equal("deleted",deleted.State);Assert.Equal("Blue Otter",Assert.Single(deleted.Spans!).Excerpt);
    }
    [Fact]
    public async Task NativeCitationDocumentsFollowToolReadsWithoutDuplicatingTheirText()
    {
        await using var native=await NativeCitationProviderFixture.CreateAsync();await using var factory=Factory(native,false);
        var actor=await Actor(factory);const string text="Orchard code name is Blue Otter.";
        var document=(await factory.Services.GetRequiredService<IDocumentRepository>().CaptureAsync(new(text,"Orchard fixture"),Authority(actor))).Document;
        native.ReadDocumentFirst=document.Id;
        var start=await factory.Services.GetRequiredService<ChatService>().StartAsync(new("Who wrote the Orchard note?",Mode:"ask"),actor);
        var final=await Finish(factory.Services.GetRequiredService<IChatRepository>(),start.Turn.Id,actor);
        Assert.Equal("complete",final.Status);Assert.Single(final.Answer!.Citations);Assert.Equal(2,native.Calls);Assert.Single(native.Documents);
        Assert.Contains(text,native.LastRequest);Assert.Equal(native.LastRequest.IndexOf(text,StringComparison.Ordinal),native.LastRequest.LastIndexOf(text,StringComparison.Ordinal));
        await using var read=await factory.Store.OpenReadConnectionAsync();
        Assert.Equal(2,await Dapper.SqlMapper.ExecuteScalarAsync<int>(read.Connection,"SELECT COUNT(*) FROM usage WHERE role='chat' AND provider='native' AND settled=1"));
    }
    [Fact]
    public async Task UnknownNativeDocumentIndexCannotBecomeAStoredCitation()
    {
        await using var native=await NativeCitationProviderFixture.CreateAsync();native.BadIndex=true;await using var factory=Factory(native,false);
        var actor=await Actor(factory);await factory.Services.GetRequiredService<IDocumentRepository>().CaptureAsync(new("Orchard code is Blue Otter.","Orchard fixture"),Authority(actor));
        var start=await factory.Services.GetRequiredService<ChatService>().StartAsync(new("Orchard code",Mode:"ask"),actor);
        var final=await Finish(factory.Services.GetRequiredService<IChatRepository>(),start.Turn.Id,actor);
        Assert.Equal("complete",final.Status);Assert.Empty(final.Answer!.Citations);Assert.Contains("no_verified_sources",final.Answer.Warnings!);Assert.True(final.Answer.InvalidCitationCount>0);
    }
    [Fact]
    public async Task PurgeRemovesNativeQuotesAndProvisionalDeltasByTheirDocumentLineage()
    {
        await using var native=await NativeCitationProviderFixture.CreateAsync();await using var factory=Factory(native,true);
        var actor=await Actor(factory);var documents=factory.Services.GetRequiredService<IDocumentRepository>();
        var document=(await documents.CaptureAsync(new("Orchard code is Blue Otter.","Orchard fixture"),Authority(actor))).Document;
        var start=await factory.Services.GetRequiredService<ChatService>().StartAsync(new("Orchard code",Mode:"ask"),actor);
        var repository=factory.Services.GetRequiredService<IChatRepository>();var final=await Finish(repository,start.Turn.Id,actor);Assert.Single(final.Answer!.Citations);
        await documents.MutateAsync(document.Id,document.Revision,"purge",JsonSerializer.SerializeToElement(new {}),Authority(actor));
        var redacted=await repository.GetTurnAsync(final.Id,actor);Assert.Empty(redacted.Answer?.Citations??[]);
        await using var read=await factory.Store.OpenReadConnectionAsync();
        Assert.Equal(0,await Dapper.SqlMapper.ExecuteScalarAsync<int>(read.Connection,"SELECT COUNT(*) FROM payloads WHERE redacted_at IS NULL AND CAST(bytes AS TEXT) LIKE '%Blue Otter%'"));
    }
    internal static LaneDWebFactory Factory(NativeCitationProviderFixture native,bool streaming,Action<SecondBrain.Core.Configuration.SecondBrainOptions>? configure=null)=>new(options=> {
        options.Providers["native"]=new() {Kind="anthropic",Endpoint=native.Endpoint.AbsoluteUri};options.Privacy.TrustedServices.Add(native.Endpoint.GetLeftPart(UriPartial.Authority));
        options.Models.Chat=new() {Provider="native",Model="native-fixture",Capabilities=new() {Tools=true,Streaming=streaming,NativeCitations=true},Limits=new() {ContextTokens=32768,MaxOutputTokens=1024}};
        configure?.Invoke(options);
    });
    internal static async Task<AuthenticatedCredential> Actor(LaneDWebFactory factory)
    {
        await factory.SeedAccountAsync();var auth=factory.Services.GetRequiredService<IAuthRepository>();
        var key=factory.Services.GetRequiredService<CredentialFactory>().CreateSession("native citation fixture",false,await auth.GetEpochAsync(),12,7);
        await auth.AddAsync(key.Record);
        return new(key.Record.Id,key.Record.Kind,key.Record.Generation,key.Record.AccountEpoch,key.Record.GrantedScopes,null);
    }
    internal static IngestionAuthority Authority(AuthenticatedCredential actor)=>new(actor.Id,actor.Generation,actor.AccountEpoch,"fixture",actor.Kind);
    internal static async Task<ChatTurn> Finish(IChatRepository repository,string id,AuthenticatedCredential actor)
    {
        ChatTurn turn=null!;for(var i=0;i<100;i++) {turn=await repository.GetTurnAsync(id,actor);if(turn.Status!="running")return turn;await Task.Delay(100);}throw new TimeoutException("Native chat did not finish: "+turn.Status);
    }
}
