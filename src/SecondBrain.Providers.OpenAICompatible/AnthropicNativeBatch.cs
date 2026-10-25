using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages.Batches;
using SecondBrain.Core.Providers;

namespace SecondBrain.Providers.OpenAICompatible;

internal sealed class AnthropicNativeBatch : IBatchProvider, IDisposable
{
    private readonly HttpClient network, transport;
    private readonly AnthropicClient sdk;
    private readonly ResolvedModel model;
    private readonly bool lifecycleOnly;
    public AnthropicNativeBatch(IProviderBinding binding,IPolicyHttpClientFactory factory,string? key,ResolvedModel model,bool lifecycleOnly=false)
    {
        this.model=model;this.lifecycleOnly=lifecycleOnly;
        network=factory.CreateClient(ModelRole.Enrich,binding);
        transport=AnthropicBinding.CreateTransport(network,key);
        sdk=new(new ClientOptions {HttpClient=transport,BaseUrl=binding.Endpoint!.AbsoluteUri.TrimEnd('/'),ApiKey=key,
            MaxRetries=0,Timeout=TimeSpan.FromSeconds(30),OpenTelemetry=false});
    }
    public Task<string?> StageAsync(string submissionKey,IReadOnlyList<BatchChatRequest> requests,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();Key(submissionKey);Validate(requests);
        // Anthropic submits the bounded request array directly, without an upload.
        return Task.FromResult<string?>("inline-"+submissionKey);
    }
    public Task<string?> FindStagedAsync(string submissionKey,CancellationToken token=default)
    {token.ThrowIfCancellationRequested();return Task.FromResult<string?>("inline-"+Key(submissionKey));}
    public Task<BatchReceipt> SubmitAsync(string submissionKey,string? stagedFileId,IReadOnlyList<BatchChatRequest> requests,CancellationToken token=default)
    {
        Key(submissionKey);Validate(requests);
        if(stagedFileId!="inline-"+submissionKey) throw new ArgumentException("Batch staging identity changed.");
        var entries=requests.Select(request=> {
            var parameters=new Dictionary<string,object?> {
                ["model"]=model.Model,["max_tokens"]=request.MaxOutputTokens,
                ["messages"]=new[]{new{role="user",content=request.Input}}
            };
            var system=new Dictionary<string,object?> { ["type"]="text",["text"]=request.System };
            if(model.Capabilities.PromptCaching) system["cache_control"]=new {type="ephemeral"};
            parameters["system"]=new[]{system};
            var output=new Dictionary<string,object?>();
            if(model.Capabilities.StructuredOutput && request.Schema is { } schema)
                output["format"]=new {type="json_schema",schema=SupportedSchema(schema)};
            if(model.Capabilities.ReasoningControl && request.Reasoning is { } reasoning) {
                parameters["thinking"]=new {type="adaptive"};output["effort"]=reasoning;
            }
            if(output.Count>0) parameters["output_config"]=output;
            return new {custom_id=RemoteId(submissionKey,request.Id),@params=parameters};
        }).ToArray();
        var body=new Dictionary<string,JsonElement> { ["requests"]=JsonSerializer.SerializeToElement(entries) };
        return Call(async ct=> {
            var response=await sdk.Messages.Batches.Create(BatchCreateParams.FromRawUnchecked(new Dictionary<string,JsonElement>(),new Dictionary<string,JsonElement>(),body),ct).ConfigureAwait(false);
            return new BatchReceipt(Id(response.ID),stagedFileId);
        },token);
    }
    public Task<BatchReceipt?> FindAsync(string submissionKey,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();Key(submissionKey);
        // The native API has no batch-level client metadata or idempotency lookup.
        // Do not enumerate unrelated workspace results or invent a successful match.
        return Task.FromResult<BatchReceipt?>(null);
    }
    public Task<BatchSnapshot> PollAsync(BatchReceipt receipt,CancellationToken token=default)
    {
        Id(receipt.Id);
        return Call(async ct=> {
            var batch=await sdk.Messages.Batches.Retrieve(receipt.Id,cancellationToken:ct).ConfigureAwait(false);
            if(Id(batch.ID)!=receipt.Id) throw Malformed();
            var json=JsonSerializer.SerializeToElement(batch);
            var status=json.GetProperty("processing_status").GetString();
            if(status is not ("in_progress" or "canceling" or "ended")) throw Malformed();
            // The result URL supplied by the provider is never followed. SDK routes use our pinned base URL.
            return new BatchSnapshot(receipt,status,status=="ended");
        },token);
    }
    public async Task CancelAsync(BatchReceipt receipt,CancellationToken token=default)
    {
        Id(receipt.Id);
        await Call(async ct=> {await sdk.Messages.Batches.Cancel(receipt.Id,cancellationToken:ct).ConfigureAwait(false);return true;},token).ConfigureAwait(false);
    }
    public Task<IReadOnlyList<BatchChatResult>> CollectAsync(BatchSnapshot snapshot,IReadOnlyList<string> expectedIds,CancellationToken token=default)
    {
        Id(snapshot.Receipt.Id);
        if(!snapshot.Terminal || expectedIds.Count is <1 or >64 || expectedIds.Distinct(StringComparer.Ordinal).Count()!=expectedIds.Count)
            throw new ArgumentException("Invalid batch collection.");
        var key=StagedKey(snapshot.Receipt.InputFileId);
        var expected=expectedIds.ToDictionary(id=>RemoteId(key,Key(id)),id=>id,StringComparer.Ordinal);
        return Call<IReadOnlyList<BatchChatResult>>(async ct=> {
            var results=new Dictionary<string,BatchChatResult>(StringComparer.Ordinal);
            using var response=await sdk.Messages.Batches.WithRawResponse.ResultsStreaming(snapshot.Receipt.Id,cancellationToken:ct).ConfigureAwait(false);
            await using var stream=await response.ReadAsStream(ct).ConfigureAwait(false);
            using var reader=new StreamReader(stream,new UTF8Encoding(false,true),detectEncodingFromByteOrderMarks:false,leaveOpen:true);
            string? line;var count=0;
            while((line=await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null) {
                if(++count>128 || Encoding.UTF8.GetByteCount(line)>131072) throw Malformed();
                if(string.IsNullOrWhiteSpace(line)) continue;
                using var json=JsonDocument.Parse(line,new JsonDocumentOptions {MaxDepth=16});var root=json.RootElement;
                var remote=root.GetProperty("custom_id").GetString();
                if(remote is null || !expected.TryGetValue(remote,out var id) || results.ContainsKey(id)) throw Malformed();
                results.Add(id,Result(root.GetProperty("result"),id));
            }
            return expectedIds.Select(id=>results.GetValueOrDefault(id)??new(id,null,null,"missing_result")).ToArray();
        },token);
    }
    public async Task DeleteFilesAsync(BatchSnapshot snapshot,CancellationToken token=default)
    {
        // There is no separate upload: deleting a finished native batch removes its stored results.
        if(snapshot.Receipt.Id.Length==0) return;
        Id(snapshot.Receipt.Id);if(!snapshot.Terminal) throw new ArgumentException("Only completed batches can be deleted.");
        try {await Call(async ct=> {await sdk.Messages.Batches.Delete(snapshot.Receipt.Id,cancellationToken:ct).ConfigureAwait(false);return true;},token).ConfigureAwait(false);}
        catch(ProviderRequestException error) when(error.StatusCode==System.Net.HttpStatusCode.NotFound) { }
    }
    private static BatchChatResult Result(JsonElement result,string id)
    {
        var type=result.GetProperty("type").GetString();
        if(type is "errored" or "canceled" or "expired") return new(id,null,null,type=="errored"?"request_failed":type);
        if(type!="succeeded") throw Malformed();
        var message=result.GetProperty("message");ModelUsageTotals? usage=null;
        if(message.TryGetProperty("usage",out var u)&&u.ValueKind==JsonValueKind.Object) {
            static long? Count(JsonElement value,string name)=>value.TryGetProperty(name,out var element)&&element.ValueKind==JsonValueKind.Number&&element.TryGetInt64(out var n)&&n>=0&&n<=100_000_000?n:null;
            var cached=Count(u,"cache_read_input_tokens");var primary=Count(u,"input_tokens");
            var input=primary is { } count?count+(cached??0)+(Count(u,"cache_creation_input_tokens")??0):(long?)null;var output=Count(u,"output_tokens");
            if(!new[]{"input_tokens","output_tokens","cache_read_input_tokens","cache_creation_input_tokens"}.Any(name=>u.TryGetProperty(name,out var value)&&value.ValueKind!=JsonValueKind.Null&&Count(u,name) is null))
                usage=new(input,output,cached,input is not null&&output is not null?input+output:null);
        }
        var stop=message.GetProperty("stop_reason").GetString();
        if(stop is not ("end_turn" or "stop_sequence")) return new(id,null,usage,"incomplete_result");
        var text=new StringBuilder();
        foreach(var content in message.GetProperty("content").EnumerateArray()) {
            var kind=content.GetProperty("type").GetString();
            if(kind=="text") text.Append(content.GetProperty("text").GetString());
            else if(kind is not ("thinking" or "redacted_thinking")) return new(id,null,usage,"unexpected_content");
        }
        var answer=text.ToString();if(Encoding.UTF8.GetByteCount(answer)>32768) throw Malformed();
        return new(id,answer,usage,null);
    }
    private void Validate(IReadOnlyList<BatchChatRequest> requests)
    {
        if(lifecycleOnly) throw new InvalidOperationException("A lifecycle binding cannot submit inference.");
        if(requests.Count is <1 or >64 || requests.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=requests.Count) throw new ArgumentException("Invalid batch size or ids.");
        foreach(var request in requests) {
            Key(request.Id);
            if(request.MaxOutputTokens is <128 or >2048 || request.MaxOutputTokens>model.Limits.MaxOutputTokens || request.System is null || request.Input is null ||
                Encoding.UTF8.GetByteCount(request.Input)>16000 || Encoding.UTF8.GetByteCount(request.System)>2048 ||
                Encoding.UTF8.GetByteCount(request.Input)+Encoding.UTF8.GetByteCount(request.System)+request.MaxOutputTokens+512>model.Limits.ContextTokens ||
                request.Reasoning is not (null or "low" or "medium" or "high") || request.Schema is { } schema && schema.GetRawText().Length>16384)
                throw new ArgumentException("Batch request exceeds its declared limits.");
        }
    }
    private static JsonNode SupportedSchema(JsonElement schema)
    {
        var node=JsonNode.Parse(schema.GetRawText(),documentOptions:new JsonDocumentOptions {MaxDepth=16})??throw new ArgumentException("Invalid schema.");
        Walk(node);return node;
        static void Walk(JsonNode node) {
            if(node is JsonObject value) {
                var constraints=new List<string>();
                foreach(var name in new[]{"maxLength","minLength","maxItems","minimum","maximum","exclusiveMinimum","exclusiveMaximum","pattern"})
                    if(value.Remove(name,out var bound)) constraints.Add(name+": "+bound);
                if(constraints.Count>0) value["description"]=(value["description"]?.GetValue<string>() is { } existing?existing+". ":"")+string.Join(", ",constraints);
                foreach(var name in new[]{"properties","$defs"})
                    if(value[name] is JsonObject members) foreach(var child in members.Select(x=>x.Value).Where(x=>x is not null)) Walk(child!);
                if(value["items"] is { } item) Walk(item);
                foreach(var name in new[]{"anyOf","allOf","oneOf"})
                    if(value[name] is JsonArray alternatives) foreach(var child in alternatives.Where(x=>x is not null)) Walk(child!);
            }
        }
    }
    private static string Key(string? value)=>value is {Length:>0 and <=100}&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_')?value:throw new ArgumentException("Invalid batch identifier.");
    private static string Id(string? value)=>value is {Length:>0 and <=200}&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_')?value:throw Malformed();
    private static string StagedKey(string? value)=>value is not null&&value.StartsWith("inline-",StringComparison.Ordinal)?Key(value[7..]):throw Malformed();
    private static string RemoteId(string key,string id)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key+"/"+id)));
    private static ProviderRequestException Malformed()=>new("https://secondbrain.dev/problems/provider-malformed","Native batch returned invalid data.");
    private static async Task<T> Call<T>(Func<CancellationToken,Task<T>> action,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try {return await action(deadline.Token).ConfigureAwait(false);}
        catch(OperationCanceledException) when(!token.IsCancellationRequested) {throw new ProviderRequestException("https://secondbrain.dev/problems/provider-timeout","Native batch request timed out.",true);}
        catch(Exception error) when(error is not OperationCanceledException) {throw AnthropicBinding.Failure(error);}
    }
    public void Dispose() {sdk.Dispose();transport.Dispose();network.Dispose();}
}
