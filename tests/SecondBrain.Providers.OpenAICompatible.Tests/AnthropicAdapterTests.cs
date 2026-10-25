using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Chat;
using System.Security.Cryptography;
using System.Text;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;
using SecondBrain.Infrastructure.Network;
using Xunit;

namespace SecondBrain.Providers.OpenAICompatible.Tests;

public sealed class AnthropicAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeChatPreservesTextBoundsAndCacheAccounting(bool streaming)
    {
        await using var f = await Fixture.Create();
        var text = new TextContent("stable prefix");
        var input = new[] {new ChatMessage(ChatRole.System, [text]), new ChatMessage(ChatRole.User, "hello")};
        var options = new ChatOptions { MaxOutputTokens = 50, Reasoning = new() {Effort=ReasoningEffort.Low} };
        var client = f.Client;
        UsageDetails usage;
        if (streaming) {
            var chunks = new List<ChatResponseUpdate>();
            await foreach(var chunk in client.GetStreamingResponseAsync(input,options)) chunks.Add(chunk);
            Assert.Equal("Native answer",string.Concat(chunks.Select(x=>x.Text)));
            usage = Assert.Single(chunks.SelectMany(x=>x.Contents).OfType<UsageContent>()).Details;
        } else {
            var answer = await client.GetResponseAsync(input, options);
            Assert.Equal("Native answer",answer.Text); usage = answer.Usage!;
        }
        Assert.Equal(22,usage.InputTokenCount); Assert.Equal(7,usage.CachedInputTokenCount);
        Assert.Equal(4,usage.OutputTokenCount); Assert.Equal(26,usage.TotalTokenCount);
        Assert.Equal(50,f.Request.GetProperty("max_tokens").GetInt32());
        Assert.Equal("claude-fixture",f.Request.GetProperty("model").GetString());
        Assert.Equal("adaptive",f.Request.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("low",f.Request.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("ephemeral",f.Request.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Null(text.AdditionalProperties);
        Assert.Equal("2026-10-21",f.Version); Assert.Null(f.Auth); Assert.Null(f.Key);
        Assert.Null(f.Ambient); Assert.Null(f.SdkHostMetadata);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolCallsUseNeutralFunctionContent(bool streaming)
    {
        await using var f=await Fixture.Create(); f.Tool=true;
        var options=new ChatOptions { Tools=[AIFunctionFactory.Create((string value)=>value,"echo")], ToolMode=ChatToolMode.RequireSpecific("echo") };
        List<FunctionCallContent> calls=[];
        if(streaming) { await foreach(var chunk in f.Client.GetStreamingResponseAsync([new(ChatRole.User,"tool")],options)) calls.AddRange(chunk.Contents.OfType<FunctionCallContent>()); }
        else calls.AddRange((await f.Client.GetResponseAsync([new(ChatRole.User,"tool")],options)).Messages.SelectMany(x=>x.Contents).OfType<FunctionCallContent>());
        var call=Assert.Single(calls); Assert.Equal("echo",call.Name); Assert.Equal("call_fixture",call.CallId);
        Assert.Equal("echo",f.Request.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("tool",f.Request.GetProperty("tool_choice").GetProperty("type").GetString());
    }

    [Fact]
    public async Task NativeCitationAnnotationsRemainAvailableWithoutInventingVerification()
    {
        await using var f=await Fixture.Create(); f.Citation=true;
        var answer=await f.Client.GetResponseAsync([new(ChatRole.User,"cite")]);
        var text=Assert.Single(answer.Messages.SelectMany(m=>m.Contents).OfType<TextContent>());
        var cite=Assert.IsType<CitationAnnotation>(Assert.Single(text.Annotations!));
        Assert.Equal("Fixture",cite.Title); Assert.Equal("source words",cite.Snippet);
        Assert.False(f.Registry.GetRole(ModelRole.Chat).Model.Capabilities.NativeCitations);
    }

    [Fact]
    public async Task SchemaAndExplicitNoThinkingSurviveMapping()
    {
        await using var f=await Fixture.Create();
        using var schema=JsonDocument.Parse("""{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"],"additionalProperties":false}""");
        await f.Client.GetResponseAsync([new(ChatRole.User,"json")], new() { ResponseFormat=ChatResponseFormat.ForJsonSchema(schema.RootElement), Reasoning=new() {Effort=ReasoningEffort.None} });
        Assert.Equal("json_schema",f.Request.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("disabled",f.Request.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(307,false)]
    [InlineData(429,true)]
    [InlineData(500,true)]
    public async Task PolicyFailuresAreSanitizedAndNotRetriedBySdk(int status,bool retryable)
    {
        await using var f=await Fixture.Create(); f.Status=status;
        var error=await Assert.ThrowsAsync<ProviderRequestException>(()=>f.Client.GetResponseAsync([new(ChatRole.User,"request")]));
        Assert.Equal(retryable,error.Retryable); Assert.Equal(1,f.Calls); Assert.Equal(0,f.Forbidden);
        Assert.DoesNotContain("private-provider-error",error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteResponseCannotSucceed(bool streaming)
    {
        await using var f=await Fixture.Create(); f.Incomplete=true;
        if(streaming) await Assert.ThrowsAsync<ProviderRequestException>(async()=> { await foreach(var _ in f.Client.GetStreamingResponseAsync([new(ChatRole.User,"request")])) {} });
        else await Assert.ThrowsAsync<ProviderRequestException>(()=>f.Client.GetResponseAsync([new(ChatRole.User,"request")]));
    }

    [Fact]
    public async Task UnsafeOverridesNeverReachTheNetwork()
    {
        await using var f=await Fixture.Create();
        foreach(var options in new[] { new ChatOptions {ModelId="other"},new ChatOptions {MaxOutputTokens=9999},new ChatOptions {AdditionalProperties=new() { ["max_tokens"]=9999 }},new ChatOptions {RawRepresentationFactory=_=>new object()} })
            await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Client.GetResponseAsync([new(ChatRole.User,"request")],options));
        Assert.Equal(0,f.Calls);
    }

    [Fact]
    public async Task DiscoveryUsesNativePathAndOnlyReadOnlyTransport()
    {
        await using var f=await Fixture.Create("/proxy"); var binding=f.Registry.GetRole(ModelRole.Chat).Provider;
        Assert.Equal(new[]{"claude-fixture"},await binding.ListModelsAsync());
        using var client=f.Transport.CreateClient(ModelRole.Chat,binding,true);
        await Assert.ThrowsAsync<PrivacyPolicyException>(()=>client.PostAsync(binding.ModelDiscoveryEndpoint,null));
        await Assert.ThrowsAsync<PrivacyPolicyException>(()=>client.GetAsync(new Uri(f.Endpoint,"v1/messages")));
        Assert.Equal(1,f.Calls);
        Assert.Equal("Native answer",(await f.Client.GetResponseAsync([new(ChatRole.User,"hello")])).Text);
    }

    [Fact]
    public async Task RemovedProviderRevokesExistingNativeClient()
    {
        await using var f=await Fixture.Create(); var client=f.Client; var next=Fixture.MakeOptions(f.Endpoint);
        next.Providers["replacement"]=next.Providers["native"]; next.Providers.Remove("native");
        next.Models.Chat!.Provider=next.Models.Enrich!.Provider="replacement"; f.Options.Reload(next);
        await Assert.ThrowsAsync<PrivacyPolicyException>(()=>client.GetResponseAsync([new(ChatRole.User,"request")]));
        Assert.Equal(0,f.Calls);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellation()
    {
        await using var f=await Fixture.Create(); f.Hold=true;
        using var timeout=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Client.GetResponseAsync([new(ChatRole.User,"request")],cancellationToken:timeout.Token));
    }

    [Fact]
    public async Task NativeProviderCannotBeUsedAsEmbedder()
    {
        await using var f=await Fixture.Create(); var options=Fixture.MakeOptions(f.Endpoint); options.Models.Embed!.Provider="native";
        Assert.Throws<InvalidOperationException>(()=>ProviderRegistry.ValidateConfiguration(options,new()));
        Assert.Null(f.Registry.GetRole(ModelRole.Chat).Provider.GetEmbeddingGenerator("claude-fixture"));
    }

    [Fact]
    public async Task ExplicitCredentialAndVersionMatchDiscoveryAndInference()
    {
        await using var f=await Fixture.Create(credential:true);
        await f.Client.GetResponseAsync([new(ChatRole.User,"request")]);
        Assert.Equal("Bearer fixture-key",f.Auth); Assert.Null(f.Key); var version=f.Version;
        await f.Registry.GetRole(ModelRole.Chat).Provider.ListModelsAsync();
        Assert.Equal("Bearer fixture-key",f.Auth); Assert.Null(f.Key); Assert.Equal(version,f.Version);
    }

    [Fact]
    public async Task UndeclaredCachingAndThinkingAreOmitted()
    {
        await using var f=await Fixture.Create(); var next=Fixture.MakeOptions(f.Endpoint);
        next.Models.Chat!.Capabilities=new() {Tools=true}; f.Options.Reload(next);
        await f.Client.GetResponseAsync([new(ChatRole.System,"stable"),new(ChatRole.User,"request")],new() {Reasoning=new() {Effort=ReasoningEffort.Low}});
        Assert.False(f.Request.TryGetProperty("thinking",out _));
        Assert.False(f.Request.GetProperty("system")[0].TryGetProperty("cache_control",out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidNativeResponsesAreSanitized(bool large)
    {
        await using var f=await Fixture.Create(); f.BadBody=!large; f.LargeBody=large;
        var error=await Assert.ThrowsAsync<ProviderRequestException>(()=>f.Client.GetResponseAsync([new(ChatRole.User,"request")]));
        Assert.EndsWith("provider-malformed",error.ProblemType); Assert.False(error.Retryable);
        Assert.DoesNotContain("private-provider-error",error.ToString());
    }

    [Fact]
    public async Task BatchLifecyclePreservesBoundsSchemaCachingAndTokenAccounting()
    {
        await using var f=await Fixture.Create(batch:true,credential:true);
        using var schema=JsonDocument.Parse("""{"type":"object","properties":{"summary":{"type":"string","maxLength":100},"maxLength":{"type":"integer"}},"required":["summary","maxLength"],"additionalProperties":false}""");
        var request=new BatchChatRequest("enrich-"+new string('a',64),"Stable prefix","Document",128,schema.RootElement,"low");
        var staged=await f.Batch.StageAsync("submission",[request]);Assert.Equal(0,f.Calls);
        Assert.Equal(staged,await f.Batch.FindStagedAsync("submission"));
        var receipt=await f.Batch.SubmitAsync("submission",staged,[request]);
        Assert.Equal("msgbatch_fixture",receipt.Id);Assert.Equal(64,f.BatchRequests[0].GetProperty("custom_id").GetString()!.Length);
        var parameters=f.BatchRequests[0].GetProperty("params");
        Assert.Equal(128,parameters.GetProperty("max_tokens").GetInt32());
        Assert.Equal("ephemeral",parameters.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("adaptive",parameters.GetProperty("thinking").GetProperty("type").GetString());
        var properties=parameters.GetProperty("output_config").GetProperty("format").GetProperty("schema").GetProperty("properties");
        Assert.True(properties.TryGetProperty("maxLength",out _));
        Assert.False(properties.GetProperty("summary").TryGetProperty("maxLength",out _));
        Assert.Contains("100",properties.GetProperty("summary").GetProperty("description").GetString());
        Assert.Equal("Bearer fixture-key",f.Auth);Assert.Null(f.Ambient);Assert.Null(f.SdkHostMetadata);
        var snapshot=await f.Batch.PollAsync(receipt);Assert.True(snapshot.Terminal);
        var result=Assert.Single(await f.Batch.CollectAsync(snapshot,[request.Id]));
        Assert.Equal("Batch answer",result.Text);Assert.Equal(22,result.Usage!.InputTokens);Assert.Equal(7,result.Usage.CachedInputTokens);Assert.Equal(26,result.Usage.TotalTokens);
        await f.Batch.CancelAsync(receipt);await f.Batch.DeleteFilesAsync(snapshot);
        Assert.Equal(1,f.BatchCancellations);Assert.Equal(1,f.BatchDeletions);Assert.Equal(0,f.Forbidden);
    }

    [Fact]
    public async Task BatchResultsAreMatchedOutOfOrderAndMissingItemsStayExplicit()
    {
        await using var f=await Fixture.Create(batch:true);f.BatchMode="partial";
        var requests=new[]{BatchRequest("first"),BatchRequest("second"),BatchRequest("third")};
        var stage=await f.Batch.StageAsync("submission",requests);var receipt=await f.Batch.SubmitAsync("submission",stage,requests);
        var results=await f.Batch.CollectAsync(await f.Batch.PollAsync(receipt),requests.Select(r=>r.Id).ToArray());
        Assert.Equal("Batch answer",results[0].Text);Assert.Equal("canceled",results[1].FailureCode);Assert.Equal("missing_result",results[2].FailureCode);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("oversized")]
    [InlineData("line")]
    [InlineData("blank")]
    public async Task BatchResponseBoundsAndIdentitiesFailClosed(string mode)
    {
        await using var f=await Fixture.Create(batch:true);var request=BatchRequest("first");
        var stage=await f.Batch.StageAsync("submission",[request]);var receipt=await f.Batch.SubmitAsync("submission",stage,[request]);
        f.BatchMode=mode;
        var error=await Assert.ThrowsAsync<ProviderRequestException>(()=>f.Batch.CollectAsync(new(receipt,"ended",true),[request.Id]));
        Assert.EndsWith("provider-malformed",error.ProblemType);Assert.DoesNotContain("private-provider-error",error.ToString());
    }

    [Fact]
    public async Task BatchTruncationAndInvalidUsageCannotInventSuccessfulTotals()
    {
        await using var f=await Fixture.Create(batch:true);var request=BatchRequest("first");
        var stage=await f.Batch.StageAsync("submission",[request]);var receipt=await f.Batch.SubmitAsync("submission",stage,[request]);
        f.BatchMode="truncated";
        var result=Assert.Single(await f.Batch.CollectAsync(new(receipt,"ended",true),[request.Id]));
        Assert.Equal("incomplete_result",result.FailureCode);Assert.NotNull(result.Usage);
        f.BatchMode="bad_usage";result=Assert.Single(await f.Batch.CollectAsync(new(receipt,"ended",true),[request.Id]));
        Assert.Equal("Batch answer",result.Text);Assert.Null(result.Usage);
    }

    [Fact]
    public async Task BatchSubmissionAmbiguityDoesNotEnumerateUnrelatedBatches()
    {
        await using var f=await Fixture.Create(batch:true);
        Assert.Null(await f.Batch.FindAsync("unknown-submission"));Assert.Equal(0,f.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchNullableUsageNeverTreatsMissingInputAsZero(bool missingInput)
    {
        await using var f=await Fixture.Create(batch:true);var request=BatchRequest("first");
        var stage=await f.Batch.StageAsync("submission",[request]);var receipt=await f.Batch.SubmitAsync("submission",stage,[request]);
        f.BatchMode=missingInput?"missing_input":"nullable_usage";
        var result=Assert.Single(await f.Batch.CollectAsync(new(receipt,"ended",true),[request.Id]));
        if(missingInput) {Assert.Null(result.Usage!.InputTokens);Assert.Null(result.Usage.TotalTokens);}
        else {Assert.Equal(10,result.Usage!.InputTokens);Assert.Equal(14,result.Usage.TotalTokens);}
    }

    [Fact]
    public async Task RepeatedBatchDeletionToleratesAnAlreadyDeletedBatch()
    {
        await using var f=await Fixture.Create(batch:true);f.BatchDeleteMissing=true;
        await f.Batch.DeleteFilesAsync(new(new("msgbatch_fixture","inline-submission"),"ended",true));
        Assert.Equal(1,f.BatchDeletions);
    }

    [Fact]
    public async Task BatchLimitsAndLifecycleOnlyBindingRejectNewSpending()
    {
        await using var f=await Fixture.Create(batch:true);
        var lifecycle=f.Registry.GetProvider("native").GetBatchLifecycleProvider("previous-model")!;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>lifecycle.StageAsync("submission",[BatchRequest("first")]));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Batch.StageAsync("submission",[BatchRequest("first") with {MaxOutputTokens=9999}]));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Batch.SubmitAsync("submission","inline-other",[BatchRequest("first")]));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Batch.DeleteFilesAsync(new(new("msgbatch_fixture"),"in_progress",false)));
        Assert.Equal(0,f.Calls);
    }

    [Theory]
    [InlineData(307,false)]
    [InlineData(429,true)]
    [InlineData(503,true)]
    public async Task BatchErrorsUsePolicyTransportWithoutSdkRetries(int status,bool retryable)
    {
        await using var f=await Fixture.Create(batch:true);f.Status=status;
        var error=await Assert.ThrowsAsync<ProviderRequestException>(()=>f.Batch.SubmitAsync("submission","inline-submission",[BatchRequest("first")]));
        Assert.Equal(retryable,error.Retryable);Assert.Equal(1,f.Calls);Assert.Equal(0,f.Forbidden);
    }

    [Fact]
    public async Task BatchClientRechecksRevokedProviderAuthority()
    {
        await using var f=await Fixture.Create(batch:true);var batch=f.Batch;var next=Fixture.MakeOptions(f.Endpoint);
        next.Providers["other"]=next.Providers["native"];next.Providers.Remove("native");next.Models.Chat!.Provider=next.Models.Enrich!.Provider="other";
        f.Options.Reload(next);
        await Assert.ThrowsAsync<PrivacyPolicyException>(()=>batch.PollAsync(new("msgbatch_fixture","inline-submission")));
        Assert.Equal(0,f.Calls);
    }

    [Fact]
    public async Task BatchResultsHonorCallerCancellation()
    {
        await using var f=await Fixture.Create(batch:true);f.Hold=true;
        using var timeout=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Batch.CollectAsync(new(new("msgbatch_fixture","inline-submission"),"ended",true),["first"],timeout.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCitationRangesSurviveBufferedAndStreamingSdkConversion(bool streaming)
    {
        await using var f=await Fixture.Create(nativeCitations:true);f.Citation=true;
        var source=CitationSource("source words and more.");var input=new[]{new ChatMessage(ChatRole.User,[new CitationSourceContent(source)])};
        var builder=new NativeCitationAnswer();builder.Register([source]);
        if(streaming) {await foreach(var update in f.Client.GetStreamingResponseAsync(input)) foreach(var item in update.Contents)builder.Append(item);}
        else {var response=await f.Client.GetResponseAsync(input);foreach(var item in response.Messages.SelectMany(m=>m.Contents))builder.Append(item);}
        var citation=Assert.Single(builder.Verify().Citations);Assert.Equal("source words",Assert.Single(citation.Spans!).Excerpt);Assert.Equal(3,citation.Source.Number);
        var document=f.Request.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("document",document.GetProperty("type").GetString());Assert.True(document.GetProperty("citations").GetProperty("enabled").GetBoolean());
        Assert.Equal(source.Excerpt,document.GetProperty("source").GetProperty("data").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnicodeCitationRangesNeedAUniqueExactSubstring(bool scalarOffsets)
    {
        await using var f=await Fixture.Create(nativeCitations:true);f.Citation=true;f.CitationQuote="The source";f.CitationStart=scalarOffsets?2:3;f.CitationEnd=scalarOffsets?12:13;
        var source=CitationSource("😀 The source words.");var result=await f.Client.GetResponseAsync([new(ChatRole.User,[new CitationSourceContent(source)])]);
        var span=Assert.Single(result.Messages.SelectMany(m=>m.Contents).OfType<CitationSpanContent>());Assert.Equal(3,span.Start);Assert.Equal(13,span.End);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("quote")]
    [InlineData("ambiguous")]
    public async Task InvalidOrAmbiguousNativeRangesDoNotBecomeCitations(string mode)
    {
        await using var f=await Fixture.Create(nativeCitations:true);f.Citation=true;var source=CitationSource("source words and more.");
        if(mode=="index")f.CitationIndex=99;if(mode=="quote")f.CitationQuote="forged quote";
        if(mode=="ambiguous") {source=CitationSource("😀aaaa");f.CitationQuote="a";f.CitationStart=2;f.CitationEnd=3;}
        var response=await f.Client.GetResponseAsync([new(ChatRole.User,[new CitationSourceContent(source)])]);
        var builder=new NativeCitationAnswer();builder.Register([source]);foreach(var content in response.Messages.SelectMany(m=>m.Contents))builder.Append(content);
        Assert.Empty(builder.Verify().Citations);Assert.Equal(1,builder.Verify().InvalidCitationCount);
    }

    [Fact]
    public async Task CitableDocumentsNeedDeclaredSupportAndCannotRequestStructuredOutput()
    {
        await using var f=await Fixture.Create(nativeCitations:true);var input=new[]{new ChatMessage(ChatRole.User,[new CitationSourceContent(CitationSource("source words"))])};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Client.GetResponseAsync(input,new() {ResponseFormat=ChatResponseFormat.Json}));
        await using var undeclared=await Fixture.Create();await Assert.ThrowsAsync<InvalidOperationException>(()=>undeclared.Client.GetResponseAsync(input));
        Assert.Equal(0,f.Calls);Assert.Equal(0,undeclared.Calls);
    }

    private static ChatSource CitationSource(string text)=>new(3,"doc",1,"generation","chunk",0,null,"Title",text,Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    private static BatchChatRequest BatchRequest(string id)=>new(id,"Stable prefix","Document",128);
    private sealed class Fixture : IAsyncDisposable
    {
        private WebApplication app=null!;
        public Uri Endpoint {get;private set;}=null!;
        public JsonElement Request; public bool Tool,Citation,Incomplete,Hold,BadBody,LargeBody; public int Calls,Forbidden,Status;
        public string? Auth,Key,Version,Ambient,SdkHostMetadata;
        public string CitationQuote="source words";public int CitationIndex,CitationStart,CitationEnd=12;
        public TestOptionsMonitor Options {get;private set;}=null!;
        public PrivacyPolicy Privacy {get;private set;}=null!;
        public PolicyHttpClientFactory Transport {get;private set;}=null!;
        public ProviderRegistry Registry {get;private set;}=null!;
        public IChatClient Client {get {var role=Registry.GetRole(ModelRole.Chat); return role.Provider.GetChatClient(role.Model.Model)!;}}
        public IBatchProvider Batch=>Registry.GetRole(ModelRole.Enrich).Provider.GetBatchProvider("claude-fixture")!;
        public JsonElement[] BatchRequests=[];public string BatchMode="normal";public int BatchCancellations,BatchDeletions;public bool BatchDeleteMissing;
        public static SecondBrainOptions MakeOptions(Uri endpoint)=>new() {
            Providers=new() { ["native"]=new() {Kind="anthropic",Endpoint=endpoint.AbsoluteUri},["embed"]=new() {Kind="openai_compatible",Endpoint=endpoint.AbsoluteUri} },
            Models=new() {
                Chat=new() {Provider="native",Model="claude-fixture",Capabilities=new() {Tools=true,Streaming=true,StructuredOutput=true,ReasoningControl=true,PromptCaching=true},Limits=new() {ContextTokens=8192,MaxOutputTokens=1024}},
                Enrich=new() {Provider="native",Model="claude-fixture",Capabilities=new() {StructuredOutput=true},Limits=new() {ContextTokens=8192,MaxOutputTokens=1024}},
                Embed=new() {Provider="embed",Model="embed",Limits=new() {EmbedDimensions=4,EmbedMaxInputTokens=8192,EmbedBatchMax=8}} },
            Privacy=new() {LocalOnly=true,EgressCanary=false,TrustedServices=[endpoint.GetLeftPart(UriPartial.Authority)]}
        };
        public static async Task<Fixture> Create(string prefix="", bool credential=false, bool batch=false, bool nativeCitations=false)
        {
            var f=new Fixture(); var builder=WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); f.app=builder.Build();
            f.app.Use(async(context,next)=> {
                if(context.Request.Path=="/forbidden") {f.Forbidden++;return;}
                f.Calls++; f.Auth=context.Request.Headers.Authorization.FirstOrDefault(); f.Key=context.Request.Headers["x-api-key"].FirstOrDefault(); f.Version=context.Request.Headers["anthropic-version"].FirstOrDefault();
                f.Ambient=context.Request.Headers["x-ambient-secret"].FirstOrDefault(); f.SdkHostMetadata=context.Request.Headers["x-stainless-os"].FirstOrDefault();
                if(f.Status!=0) {context.Response.StatusCode=f.Status;context.Response.Headers.Location="/forbidden";await context.Response.WriteAsync("private-provider-error");return;}
                await next();
            });
            f.app.MapGet(prefix+"/v1/models",()=>Results.Json(new {data=new[]{new{id="claude-fixture"}},has_more=false}));
            object BatchBody()=>new {id="msgbatch_fixture",type="message_batch",processing_status="ended",created_at="2026-10-24T12:00:00Z",expires_at="2026-10-25T12:00:00Z",results_url="https://evil.invalid/private-provider-error",request_counts=new {processing=0,succeeded=1,errored=0,canceled=0,expired=0}};
            f.app.MapPost(prefix+"/v1/messages/batches",async(HttpContext context)=> {
                using var doc=await JsonDocument.ParseAsync(context.Request.Body);f.BatchRequests=doc.RootElement.GetProperty("requests").EnumerateArray().Select(x=>x.Clone()).ToArray();
                await context.Response.WriteAsJsonAsync(BatchBody());
            });
            f.app.MapGet(prefix+"/v1/messages/batches/{id}",()=>Results.Json(BatchBody()));
            f.app.MapPost(prefix+"/v1/messages/batches/{id}/cancel",()=> {f.BatchCancellations++;return Results.Json(BatchBody());});
            f.app.MapDelete(prefix+"/v1/messages/batches/{id}",()=> {f.BatchDeletions++;return f.BatchDeleteMissing?Results.NotFound():Results.Json(new {id="msgbatch_fixture",type="message_batch_deleted"});});
            f.app.MapGet(prefix+"/v1/messages/batches/{id}/results",async(HttpContext context)=> {
                if(f.Hold) {await Task.Delay(Timeout.Infinite,context.RequestAborted);return;}
                if(f.BatchMode=="oversized") {context.Response.ContentLength=33*1024*1024;await context.Response.StartAsync();return;}
                if(f.BatchMode=="line") {await context.Response.WriteAsync(new string('x',131073));return;}
                if(f.BatchMode=="blank") {await context.Response.WriteAsync(new string('\n',129));return;}
                var requests=f.BatchMode=="partial"?f.BatchRequests.Take(2).Reverse():f.BatchRequests.Reverse();
                foreach(var (request,index) in requests.Select((request,index)=>(request,index))) {
                    var custom=request.GetProperty("custom_id").GetString();if(f.BatchMode=="unknown") custom="not-ours";
                    object usage=f.BatchMode switch {
                        "bad_usage"=>new {input_tokens=-1,output_tokens=4},
                        "nullable_usage"=>new {input_tokens=10,output_tokens=4,cache_creation_input_tokens=(long?)null,cache_read_input_tokens=(long?)null},
                        "missing_input"=>new {output_tokens=4,cache_read_input_tokens=7},
                        _=>new {input_tokens=10,output_tokens=4,cache_creation_input_tokens=5,cache_read_input_tokens=7}
                    };
                    object result=f.BatchMode=="partial"&&index==0?new {type="canceled"}:new {type="succeeded",message=new {id="msg_answer",type="message",role="assistant",model="claude-fixture",content=new[]{new{type="text",text="Batch answer"}},stop_reason=f.BatchMode=="truncated"?"max_tokens":"end_turn",usage}};
                    var line=JsonSerializer.Serialize(new {custom_id=custom,result})+"\n";
                    await context.Response.WriteAsync(line);if(f.BatchMode=="duplicate") await context.Response.WriteAsync(line);
                }
            });
            f.app.MapPost(prefix+"/v1/messages",async(HttpContext context)=> {
                using var doc=await JsonDocument.ParseAsync(context.Request.Body); f.Request=doc.RootElement.Clone();
                if(f.BadBody) {await context.Response.WriteAsync("{private-provider-error");return;}
                if(f.LargeBody) {context.Response.ContentLength=33*1024*1024;await context.Response.StartAsync();return;}
                if(f.Hold) {await Task.Delay(Timeout.Infinite,context.RequestAborted);return;}
                var streaming=f.Request.TryGetProperty("stream",out var stream)&&stream.GetBoolean();
                object block=f.Tool?new {type="tool_use",id="call_fixture",name="echo",input=new {value="hello"}}:
                    f.Citation?new {type="text",text="Native answer",citations=new[]{new{type="char_location",cited_text=f.CitationQuote,document_index=f.CitationIndex,document_title="Fixture",start_char_index=f.CitationStart,end_char_index=f.CitationEnd}}}:
                    new {type="text",text="Native answer"};
                string? stop=f.Incomplete?null:f.Tool?"tool_use":"end_turn";
                if(!streaming) {
                    await context.Response.WriteAsJsonAsync(new {id="msg_fixture",type="message",role="assistant",model="claude-fixture",content=new[]{block},stop_reason=stop,stop_sequence=(string?)null,usage=new {input_tokens=10,output_tokens=4,cache_creation_input_tokens=5,cache_read_input_tokens=7}});return;
                }
                context.Response.ContentType="text/event-stream";
                async Task Event(string type,object value)=>await context.Response.WriteAsync("event: "+type+"\ndata: "+JsonSerializer.Serialize(value)+"\n\n");
                await Event("message_start",new {type="message_start",message=new{id="msg_fixture",type="message",role="assistant",model="claude-fixture",content=Array.Empty<object>(),stop_reason=(string?)null,stop_sequence=(string?)null,usage=new{input_tokens=10,output_tokens=0,cache_creation_input_tokens=5,cache_read_input_tokens=7}}});
                if(f.Tool) {
                    await Event("content_block_start",new {type="content_block_start",index=0,content_block=new{type="tool_use",id="call_fixture",name="echo",input=new{}}});
                    await Event("content_block_delta",new{type="content_block_delta",index=0,delta=new{type="input_json_delta",partial_json="{\"value\":\"hello\"}"}});
                } else {
                    await Event("content_block_start",new{type="content_block_start",index=0,content_block=new{type="text",text=""}});
                    await Event("content_block_delta",new{type="content_block_delta",index=0,delta=new{type="text_delta",text="Native answer"}});
                    if(f.Citation) await Event("content_block_delta",new {type="content_block_delta",index=0,delta=new {type="citations_delta",citation=new {type="char_location",cited_text=f.CitationQuote,document_index=f.CitationIndex,document_title="Fixture",start_char_index=f.CitationStart,end_char_index=f.CitationEnd}}});
                }
                await Event("content_block_stop",new{type="content_block_stop",index=0});
                await Event("message_delta",new{type="message_delta",delta=new{stop_reason=f.Tool?"tool_use":"end_turn",stop_sequence=(string?)null},usage=new{output_tokens=4}});
                if(!f.Incomplete) await Event("message_stop",new{type="message_stop"});
            });
            await f.app.StartAsync(); f.Endpoint=new(f.app.Urls.Single()+prefix+"/"); var settings=MakeOptions(f.Endpoint);
            if(batch) settings.Models.Enrich!.Capabilities=new() {Batch=true,StructuredOutput=true,PromptCaching=true,ReasoningControl=true};
            if(nativeCitations) settings.Models.Chat!.Capabilities=settings.Models.Chat.Capabilities with {NativeCitations=true};
            f.Options=new(settings);
            var dns=new SystemDnsResolver();f.Privacy=new(f.Options,dns);f.Transport=new(f.Privacy,dns);f.Registry=new(f.Options,f.Transport,f.Privacy,new(),new FixtureCredentials(credential));return f;
        }
        private sealed class FixtureCredentials(bool enabled) : IProviderCredentialResolver { public string? Resolve(string? reference)=>enabled?"fixture-key":null; }
        public async ValueTask DisposeAsync() {Registry.Dispose();Transport.Dispose();Privacy.Dispose();await app.StopAsync();await app.DisposeAsync();}
    }
}
