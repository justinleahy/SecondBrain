using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;
using SecondBrain.Infrastructure.Network;
using Xunit;

namespace SecondBrain.Providers.OpenAICompatible.Tests;

public sealed class OpenAIBatchTests
{
    [Fact]
    public async Task NativeLifecycleStagesSubmitsRecoversCollectsAndCleansUp()
    {
        await using var f=await Fixture.Create();var requests=new[]{Request("jobA"),Request("jobB"),Request("jobC")};
        var file=await f.Batch.StageAsync("submission123",requests);
        Assert.Equal("file_input",file);Assert.Equal(3,f.Input.Length);
        Assert.Equal("batch",f.UploadPurpose);Assert.Equal("secondbrain-submission123.jsonl",f.Filename);
        Assert.All(f.Input,x=> {Assert.Equal("/v1/chat/completions",x.GetProperty("url").GetString());Assert.Equal(128,x.GetProperty("body").GetProperty("max_completion_tokens").GetInt32());});
        Assert.Equal(file,await f.Batch.FindStagedAsync("submission123"));
        var receipt=await f.Batch.SubmitAsync("submission123",file,requests);
        Assert.Equal("submission123",f.Submission.GetProperty("metadata").GetProperty("secondbrain_submission").GetString());
        Assert.Equal("24h",f.Submission.GetProperty("completion_window").GetString());
        Assert.Equal(receipt,await f.Batch.FindAsync("submission123"));
        var snapshot=await f.Batch.PollAsync(receipt);Assert.True(snapshot.Terminal);
        var results=await f.Batch.CollectAsync(snapshot,requests.Select(x=>x.Id).ToArray());
        Assert.Equal("request_failed",results[0].FailureCode);Assert.Equal("Answer",results[1].Text);
        Assert.Equal(15,results[1].Usage!.TotalTokens);Assert.Equal("missing_result",results[2].FailureCode);
        await f.Batch.CancelAsync(receipt);Assert.Equal(1,f.Cancellations);
        await f.Batch.DeleteFilesAsync(snapshot);Assert.Equal(3,f.Deletions);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("newline_bomb")]
    public async Task InvalidOrUnboundedResultsFailClosed(string mode)
    {
        await using var f=await Fixture.Create();f.ResultMode=mode;
        var snapshot=new BatchSnapshot(new("batch_native","file_input"),"completed",true,"file_output");
        var error=await Assert.ThrowsAsync<ProviderRequestException>(()=>f.Batch.CollectAsync(snapshot,["jobA","jobB"]));
        Assert.EndsWith("provider-malformed",error.ProblemType);Assert.DoesNotContain("private-result",error.ToString());
    }

    [Theory]
    [InlineData(307,false)]
    [InlineData(429,true)]
    [InlineData(503,true)]
    public async Task BatchTrafficKeepsPolicyErrorsAndDoesNotRetry(int status,bool retryable)
    {
        await using var f=await Fixture.Create();f.FailureStatus=status;
        var error=await Assert.ThrowsAsync<ProviderRequestException>(()=>f.Batch.StageAsync("submission123",[Request("jobA")]));
        Assert.Equal(retryable,error.Retryable);Assert.Equal(1,f.Calls);Assert.Equal(0,f.Forbidden);
        Assert.DoesNotContain("private-result",error.Message);
    }

    [Fact]
    public async Task RequestsCannotEscapeBatchLimitsOrInjectPaths()
    {
        await using var f=await Fixture.Create();
        foreach(var request in new[]{Request("../escape"),Request("jobA") with {MaxOutputTokens=9999},Request("jobA") with {Input=new string('x',16001)}})
            await Assert.ThrowsAsync<ArgumentException>(()=>f.Batch.StageAsync("submission123",[request]));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Batch.StageAsync("submission123",[Request("duplicate"),Request("duplicate")]));
        await Assert.ThrowsAsync<ProviderRequestException>(()=>f.Batch.PollAsync(new("../escape")));
        Assert.Equal(0,f.Calls);
    }

    [Fact]
    public async Task UndeclaredBatchSupportIsUnavailable()
    {
        await using var f=await Fixture.Create();var options=Fixture.Options(f.Endpoint);options.Models.Enrich!.Capabilities=new();f.Monitor.Reload(options);
        Assert.Null(f.Registry.GetRole(ModelRole.Enrich).Provider.GetBatchProvider("model"));
        options.Models.Enrich.Capabilities=new() {Batch=true};options.Providers["native"].Kind="openai_compatible";
        Assert.Throws<InvalidOperationException>(()=>ProviderRegistry.ValidateConfiguration(options,new()));
    }

    [Fact]
    public async Task LifecycleOnlyBindingCannotSubmitButCanCleanOldModels()
    {
        await using var f=await Fixture.Create();var lifecycle=f.Registry.GetProvider("native").GetBatchLifecycleProvider("old-model")!;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>lifecycle.StageAsync("submission123",[Request("jobA")]));
        await lifecycle.CancelAsync(new("batch_native"));Assert.Equal(1,f.Cancellations);
    }

    [Fact]
    public async Task PolicyRevocationAppliesToAlreadyCreatedBatchClient()
    {
        await using var f=await Fixture.Create();var batch=f.Batch;var options=Fixture.Options(f.Endpoint);
        options.Providers["replacement"]=options.Providers["native"];options.Providers.Remove("native");
        options.Models.Chat!.Provider=options.Models.Enrich!.Provider=options.Models.Embed!.Provider="replacement";f.Monitor.Reload(options);
        await Assert.ThrowsAsync<PrivacyPolicyException>(()=>batch.PollAsync(new("batch_native")));Assert.Equal(0,f.Calls);
    }

    [Fact]
    public async Task CancellationReachesTheBoundedDownload()
    {
        await using var f=await Fixture.Create();f.Hold=true;
        using var timeout=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Batch.CollectAsync(new(new("batch_native"),"completed",true,"file_output"),["jobB"],timeout.Token));
    }

    private static BatchChatRequest Request(string id)=>new(id,"Summarize data","Document data",128);
    private sealed class Fixture : IAsyncDisposable
    {
        private WebApplication app=null!;
        public Uri Endpoint {get;private set;}=null!;
        public TestOptionsMonitor Monitor {get;private set;}=null!;
        public ProviderRegistry Registry {get;private set;}=null!;
        private PrivacyPolicy privacy=null!;private PolicyHttpClientFactory factory=null!;
        public IBatchProvider Batch=>Registry.GetRole(ModelRole.Enrich).Provider.GetBatchProvider("model")!;
        public int Calls,Forbidden,FailureStatus,Deletions,Cancellations;public bool Hold;
        public string ResultMode="normal",UploadPurpose="",Filename="";public JsonElement[] Input=[];public JsonElement Submission;
        private string Key="submission123";
        public static SecondBrainOptions Options(Uri endpoint)=>new() {
            Providers=new() { ["native"]=new() {Kind="openai",Endpoint=endpoint.AbsoluteUri} },
            Models=new() {
                Chat=new() {Provider="native",Model="model",Capabilities=new() {Tools=true},Limits=new() {ContextTokens=8192,MaxOutputTokens=1024}},
                Enrich=new() {Provider="native",Model="model",Capabilities=new() {Batch=true},Limits=new() {ContextTokens=8192,MaxOutputTokens=1024}},
                Embed=new() {Provider="native",Model="embed",Limits=new() {EmbedDimensions=4,EmbedMaxInputTokens=8192,EmbedBatchMax=8}} },
            Privacy=new() {LocalOnly=true,EgressCanary=false,TrustedServices=[endpoint.GetLeftPart(UriPartial.Authority)]}
        };
        private object BatchValue()=>new {id="batch_native",@object="batch",status="completed",endpoint="/v1/chat/completions",completion_window="24h",created_at=1,input_file_id="file_input",output_file_id="file_output",error_file_id="file_error",metadata=new {secondbrain_submission=Key},request_counts=new{total=3,completed=1,failed=1}};
        public static async Task<Fixture> Create()
        {
            var f=new Fixture();var builder=WebApplication.CreateBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");f.app=builder.Build();
            f.app.Use(async(context,next)=> {
                if(context.Request.Path=="/forbidden") {f.Forbidden++;return;}
                f.Calls++;if(f.FailureStatus!=0) {context.Response.StatusCode=f.FailureStatus;context.Response.Headers.Location="/forbidden";await context.Response.WriteAsync("private-result");return;}
                await next();
            });
            f.app.MapPost("/v1/files",async(HttpContext context)=> {
                var form=await context.Request.ReadFormAsync();f.UploadPurpose=form["purpose"]!;var file=Assert.Single(form.Files);f.Filename=file.FileName;
                using var reader=new StreamReader(file.OpenReadStream());f.Input=(await reader.ReadToEndAsync()).Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
                await context.Response.WriteAsJsonAsync(new {id="file_input",@object="file",bytes=file.Length,created_at=1,filename=f.Filename,purpose="batch",status="processed"});
            });
            f.app.MapGet("/v1/files",()=>Results.Json(new { @object="list",data=new[]{new {id="file_input",@object="file",bytes=10,created_at=1,filename=f.Filename,purpose="batch",status="processed"}},has_more=false }));
            f.app.MapPost("/v1/batches",async(HttpContext context)=> {
                f.Submission=await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body);f.Key=f.Submission.GetProperty("metadata").GetProperty("secondbrain_submission").GetString()!;
                await context.Response.WriteAsJsonAsync(f.BatchValue());
            });
            f.app.MapGet("/v1/batches",()=>Results.Json(new {@object="list",data=new[]{f.BatchValue()},has_more=false,first_id="batch_native",last_id="batch_native"}));
            f.app.MapGet("/v1/batches/{id}",()=>Results.Json(f.BatchValue()));
            f.app.MapPost("/v1/batches/{id}/cancel",()=> {f.Cancellations++;return Results.Json(f.BatchValue());});
            f.app.MapDelete("/v1/files/{id}",(string id)=> {f.Deletions++;return Results.Json(new {id,@object="file",deleted=true});});
            f.app.MapGet("/v1/files/{id}/content",async(HttpContext context,string id)=> {
                if(f.Hold) {await Task.Delay(Timeout.Infinite,context.RequestAborted);return;}
                if(f.ResultMode=="oversized") {context.Response.ContentLength=33*1024*1024;await context.Response.StartAsync();return;}
                object Success(string key)=>new {custom_id=key,response=new {status_code=200,body=new{choices=new[]{new{message=new{role="assistant",content="Answer"},finish_reason="stop"}},usage=new{prompt_tokens=11,completion_tokens=4,total_tokens=15}}},error=(object?)null};
                var line=id=="file_error"?JsonSerializer.Serialize(new {custom_id="jobA",error=new{message="private-result"},response=(object?)null}):JsonSerializer.Serialize(Success(f.ResultMode=="unknown"?"jobUnknown":"jobB"));
                if(f.ResultMode=="duplicate")line+="\n"+JsonSerializer.Serialize(Success("jobB"));
                if(f.ResultMode=="malformed")line="{private-result";
                if(f.ResultMode=="newline_bomb")line=new string('\n',129);
                await context.Response.WriteAsync(line+"\n");
            });
            await f.app.StartAsync();f.Endpoint=new(f.app.Urls.Single()+"/v1");f.Monitor=new(Options(f.Endpoint));var dns=new SystemDnsResolver();
            f.privacy=new(f.Monitor,dns);f.factory=new(f.privacy,dns);f.Registry=new(f.Monitor,f.factory,f.privacy,new(),new EnvironmentProviderCredentialResolver());return f;
        }
        public async ValueTask DisposeAsync() {Registry.Dispose();factory.Dispose();privacy.Dispose();await app.StopAsync();await app.DisposeAsync();}
    }
}
