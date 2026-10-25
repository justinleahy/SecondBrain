using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;
using SecondBrain.Infrastructure.Network;
using Xunit;

namespace SecondBrain.Providers.OpenAICompatible.Tests;

public sealed class OllamaAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeChatPreservesTextUsageAndBoundedOptions(bool streaming)
    {
        await using var f = await Fixture.Create();
        var role = f.Registry.GetRole(ModelRole.Chat); var client = role.Provider.GetChatClient(role.Model.Model)!;
        var options = new ChatOptions { MaxOutputTokens = 45, Temperature = .2f, Reasoning = new() { Effort = ReasoningEffort.Low }, ResponseFormat = ChatResponseFormat.Json };
        if (streaming)
        {
            var chunks = new List<ChatResponseUpdate>();
            await foreach (var chunk in client.GetStreamingResponseAsync([new(ChatRole.User, "hello")], options)) chunks.Add(chunk);
            Assert.Equal("Native response", string.Concat(chunks.Select(c => c.Text)));
            Assert.Contains(chunks.SelectMany(c => c.Contents), c => c is UsageContent);
        }
        else
        {
            var response = await client.GetResponseAsync([new(ChatRole.User, "hello")], options);
            Assert.Equal("Native response", response.Text); Assert.Equal(12, response.Usage!.InputTokenCount); Assert.Equal(3, response.Usage.OutputTokenCount);
        }
        Assert.Equal("native-chat", f.Request.GetProperty("model").GetString());
        Assert.Equal(45, f.Request.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(8192, f.Request.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal("json", f.Request.GetProperty("format").GetString());
        Assert.Equal("low", f.Request.GetProperty("think").GetString());
    }

    [Fact]
    public async Task NativeToolsRemainNeutralFunctionCalls()
    {
        await using var f = await Fixture.Create(); f.Tool = true;
        var role = f.Registry.GetRole(ModelRole.Chat); var client = role.Provider.GetChatClient(role.Model.Model)!;
        var response = await client.GetResponseAsync([new(ChatRole.User, "call")], new() { Tools = [AIFunctionFactory.Create((string value) => value, "echo")] });
        var call = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal("echo", call.Name); Assert.NotNull(call.Arguments); Assert.Contains("tools", f.Request.ToString());
    }

    [Fact]
    public async Task NativeEmbeddingSendsDimensionsAndRefusesTruncation()
    {
        await using var f = await Fixture.Create(); var role = f.Registry.GetRole(ModelRole.Embed);
        var generator = role.Provider.GetEmbeddingGenerator(role.Model.Model)!;
        var response = await generator.GenerateAsync(["first", "second"], new() { AdditionalProperties = new() { ["intent"] = "document" } });
        Assert.Equal(2, response.Count); Assert.All(response, vector => Assert.Equal(4, vector.Vector.Length));
        Assert.Equal(8, response.Usage!.TotalTokenCount);
        Assert.False(f.Request.GetProperty("truncate").GetBoolean()); Assert.Equal(4, f.Request.GetProperty("dimensions").GetInt32());
        Assert.Equal(2, f.Request.GetProperty("input").GetArrayLength());
        Assert.Equal(new[] { "native-chat", "native-embed" }, await role.Provider.ListModelsAsync());
    }

    [Fact]
    public async Task WrongEmbeddingShapeAndNativeErrorsAreTypedAndSanitized()
    {
        await using var f = await Fixture.Create(); f.WrongDimensions = true;
        var role = f.Registry.GetRole(ModelRole.Embed);
        var error = await Assert.ThrowsAsync<ProviderRequestException>(() => role.Provider.GetEmbeddingGenerator(role.Model.Model)!.GenerateAsync(["text"]));
        Assert.EndsWith("provider-malformed", error.ProblemType); Assert.False(error.Retryable);
        f.WrongDimensions = false; f.NativeError = true;
        var chat = f.Registry.GetRole(ModelRole.Chat);
        error = await Assert.ThrowsAsync<ProviderRequestException>(() => chat.Provider.GetChatClient(chat.Model.Model)!.GetResponseAsync([new(ChatRole.User,"text")]));
        Assert.DoesNotContain("private-error-body", error.Message);
    }

    [Fact]
    public async Task RawOptionsAndEmbeddingSpaceOverridesAreRejectedBeforeSending()
    {
        await using var f = await Fixture.Create(); var chat = f.Registry.GetRole(ModelRole.Chat); var client = chat.Provider.GetChatClient(chat.Model.Model)!;
        foreach (var options in new[] { new ChatOptions { ModelId="other" }, new ChatOptions { MaxOutputTokens=9999 }, new ChatOptions { AdditionalProperties=new() { ["num_ctx"]=100000 } } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new(ChatRole.User,"text")], options));
        var embed = f.Registry.GetRole(ModelRole.Embed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => embed.Provider.GetEmbeddingGenerator(embed.Model.Model)!.GenerateAsync(["text"], new() { Dimensions=8 }));
        Assert.Equal(0, f.Calls);
    }

    [Theory]
    [InlineData(307,false)]
    [InlineData(429,true)]
    [InlineData(500,true)]
    public async Task NativeRequestsKeepPolicyRedirectAndRetrySemantics(int status, bool retryable)
    {
        await using var f = await Fixture.Create(); f.Status = status;
        var role = f.Registry.GetRole(ModelRole.Chat);
        var error = await Assert.ThrowsAsync<ProviderRequestException>(() => role.Provider.GetChatClient(role.Model.Model)!.GetResponseAsync([new(ChatRole.User,"text")]));
        Assert.Equal(retryable, error.Retryable); Assert.Equal(1, f.Calls); Assert.Equal(0,f.ForbiddenCalls);
    }

    [Fact]
    public async Task DiscoveryIsReadOnlyAndRetiredBindingsRevalidatePolicy()
    {
        await using var f = await Fixture.Create(); var old = f.Registry.GetRole(ModelRole.Chat).Provider;
        using var discovery = f.Transport.CreateClient(ModelRole.Chat, old, true);
        await Assert.ThrowsAsync<PrivacyPolicyException>(() => discovery.GetAsync(new Uri(f.Endpoint,"api/chat")));
        Assert.Equal(0, f.Calls);
        var options = Fixture.MakeOptions(f.Endpoint); options.Providers["other"] = options.Providers["native"]; options.Providers.Remove("native");
        options.Models.Chat!.Provider=options.Models.Enrich!.Provider=options.Models.Embed!.Provider="other";
        f.Options.Reload(options);
        await Assert.ThrowsAsync<PrivacyPolicyException>(() => old.GetChatClient("native-chat")!.GetResponseAsync([new(ChatRole.User,"text")]));
        Assert.Equal(0,f.Calls);
    }

    [Fact]
    public async Task ProtocolSwitchCreatesANewEmbeddingIdentity()
    {
        await using var f = await Fixture.Create(); var before=f.Registry.GetRole(ModelRole.Embed);
        var original=EmbeddingFingerprint.Create("native",before.Provider.EmbeddingIdentity,"native-embed",4);
        var options=Fixture.MakeOptions(f.Endpoint); options.Providers["native"].Kind="openai_compatible"; f.Options.Reload(options);
        var after=f.Registry.GetRole(ModelRole.Embed);
        Assert.NotEqual(original,EmbeddingFingerprint.Create("native",after.Provider.EmbeddingIdentity,"native-embed",4));
        Assert.Same(before.Provider,f.Registry.GetEmbeddingProvider("native","native-embed",4,original));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteNativeResponsesFailInsteadOfReportingSuccess(bool streaming)
    {
        await using var f=await Fixture.Create(); f.Incomplete=true;
        var role=f.Registry.GetRole(ModelRole.Chat); var client=role.Provider.GetChatClient(role.Model.Model)!;
        if(streaming) await Assert.ThrowsAsync<ProviderRequestException>(async()=> {await foreach(var _ in client.GetStreamingResponseAsync([new(ChatRole.User,"text")])) { }});
        else await Assert.ThrowsAsync<ProviderRequestException>(()=>client.GetResponseAsync([new(ChatRole.User,"text")]));
    }

    [Fact]
    public async Task NativeDiscoveryBoundsAreEnforcedBeforeBodyRead()
    {
        await using var f=await Fixture.Create(); f.LargeModels=true;
        var error=await Assert.ThrowsAsync<ProviderRequestException>(()=>f.Registry.GetRole(ModelRole.Chat).Provider.ListModelsAsync().AsTask());
        Assert.EndsWith("provider-malformed",error.ProblemType); Assert.False(error.Retryable);
    }

    [Fact]
    public async Task CallerCancellationStopsTheNativeRequest()
    {
        await using var f=await Fixture.Create(); f.Hold=true;
        using var timeout=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var role=f.Registry.GetRole(ModelRole.Chat);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>role.Provider.GetChatClient(role.Model.Model)!.GetResponseAsync([new(ChatRole.User,"text")],cancellationToken:timeout.Token));
    }

    [Fact]
    public async Task ExplicitEndpointPrefixIsPreserved()
    {
        await using var f=await Fixture.Create("/proxy");
        var role=f.Registry.GetRole(ModelRole.Enrich);
        Assert.Equal("Native response",(await role.Provider.GetChatClient(role.Model.Model)!.GetResponseAsync([new(ChatRole.User,"text")])).Text);
        Assert.Equal(2,(await role.Provider.ListModelsAsync()).Count);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private WebApplication app=null!;
        public Uri Endpoint { get; private set; }=null!;
        public JsonElement Request; public bool Tool,WrongDimensions,NativeError,Incomplete,LargeModels,Hold; public int Calls,ForbiddenCalls,Status;
        public TestOptionsMonitor Options { get; private set; }=null!;
        public PrivacyPolicy Privacy { get; private set; }=null!;
        public PolicyHttpClientFactory Transport { get; private set; }=null!;
        public ProviderRegistry Registry { get; private set; }=null!;
        public static SecondBrainOptions MakeOptions(Uri endpoint) => new()
        {
            Providers=new() { ["native"]=new() { Kind="ollama",Endpoint=endpoint.AbsoluteUri } },
            Models=new() {
                Chat=new() { Provider="native",Model="native-chat",Capabilities=new() { Tools=true,Streaming=true,StructuredOutput=true,ReasoningControl=true },Limits=new() { ContextTokens=8192,MaxOutputTokens=1024 } },
                Enrich=new() { Provider="native",Model="native-chat",Capabilities=new() { StructuredOutput=true },Limits=new() { ContextTokens=8192,MaxOutputTokens=1024 } },
                Embed=new() { Provider="native",Model="native-embed",Limits=new() { EmbedDimensions=4,EmbedMaxInputTokens=8192,EmbedBatchMax=8 } } },
            Privacy=new() { LocalOnly=true,EgressCanary=false,TrustedServices=[endpoint.GetLeftPart(UriPartial.Authority)] }
        };
        public static async Task<Fixture> Create(string prefix="")
        {
            var f=new Fixture(); var builder=WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); f.app=builder.Build();
            f.app.Use(async(context,next)=> {
                if(context.Request.Path=="/forbidden") { f.ForbiddenCalls++; await next(); return; }
                f.Calls++; if(f.Status!=0) { context.Response.StatusCode=f.Status; context.Response.Headers.Location="/forbidden"; return; }
                await next();
            });
            f.app.MapGet(prefix+"/api/tags",async(HttpContext context)=> {
                if(f.LargeModels) {context.Response.ContentLength=9*1024*1024; await context.Response.StartAsync(); return;}
                await context.Response.WriteAsJsonAsync(new {models=new[]{new{name="native-chat"},new{name="native-embed"}}});
            });
            f.app.MapGet("/forbidden",()=>Results.Ok());
            f.app.MapPost(prefix+"/api/embed", async(HttpContext context)=> {
                using var body=await JsonDocument.ParseAsync(context.Request.Body); f.Request=body.RootElement.Clone();
                var count=f.Request.GetProperty("input").GetArrayLength(); var dimensions=f.WrongDimensions?3:f.Request.GetProperty("dimensions").GetInt32();
                await context.Response.WriteAsJsonAsync(new { model="native-embed",embeddings=Enumerable.Range(0,count).Select(_=>Enumerable.Repeat(.5f,dimensions).ToArray()).ToArray(),prompt_eval_count=8 });
            });
            f.app.MapPost(prefix+"/api/chat",async(HttpContext context)=> {
                using var body=await JsonDocument.ParseAsync(context.Request.Body); f.Request=body.RootElement.Clone();
                if(f.Hold) {await Task.Delay(Timeout.Infinite,context.RequestAborted);return;}
                if(f.Incomplete) {await context.Response.WriteAsync(JsonSerializer.Serialize(new {model="native-chat",message=new{role="assistant",content="Partial"},done=false})+"\n");return;}
                if(f.NativeError) { await context.Response.WriteAsJsonAsync(new {error="private-error-body"}); return; }
                object message=f.Tool?new {role="assistant",content="",tool_calls=new[] {new { function=new {name="echo",arguments=new { value="hello" }}}}}:new {role="assistant",content="Native response"};
                if(f.Request.GetProperty("stream").GetBoolean()) {
                    context.Response.ContentType="application/x-ndjson";
                    foreach(var text in new[]{"Native ","response"}) await context.Response.WriteAsync(JsonSerializer.Serialize(new {model="native-chat",created_at="2026-10-24T12:00:00Z",message=new {role="assistant",content=text},done=false})+"\n");
                    message=new {role="assistant",content=""};
                }
                await context.Response.WriteAsync(JsonSerializer.Serialize(new {model="native-chat",created_at="2026-10-24T12:00:00Z",message,done=true,done_reason="stop",prompt_eval_count=12,eval_count=3})+"\n");
            });
            await f.app.StartAsync(); f.Endpoint=new(f.app.Urls.Single()+prefix+"/"); f.Options=new(MakeOptions(f.Endpoint));
            var dns=new SystemDnsResolver(); f.Privacy=new(f.Options,dns); f.Transport=new(f.Privacy,dns); f.Registry=new(f.Options,f.Transport,f.Privacy,new(),new EnvironmentProviderCredentialResolver()); return f;
        }
        public async ValueTask DisposeAsync() { Registry.Dispose();Transport.Dispose();Privacy.Dispose();await app.StopAsync();await app.DisposeAsync(); }
    }
}
