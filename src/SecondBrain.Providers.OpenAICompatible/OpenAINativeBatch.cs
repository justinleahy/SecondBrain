#pragma warning disable OPENAI001 // Pinned SDK's native Batch API is experimental; isolated to this adapter.
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Batch;
using OpenAI.Files;
using SecondBrain.Core.Providers;

namespace SecondBrain.Providers.OpenAICompatible;

internal sealed class OpenAINativeBatch : IBatchProvider, IDisposable
{
    private readonly HttpClient network, transport;
    private readonly OpenAIClient sdk;
    private readonly ResolvedModel model;
    private readonly bool lifecycleOnly;
    public OpenAINativeBatch(IProviderBinding binding, IPolicyHttpClientFactory factory, string? key, ResolvedModel model, bool lifecycleOnly = false)
    {
        this.model = model; this.lifecycleOnly = lifecycleOnly;
        network = factory.CreateClient(ModelRole.Enrich, binding);
        transport = new(new LimitedProviderHandler(network)) { Timeout = TimeSpan.FromSeconds(30) };
        sdk = new(new ApiKeyCredential(key ?? "no-key-configured"), new() {
            Endpoint = binding.Endpoint, Transport = new HttpClientPipelineTransport(transport),
            RetryPolicy = new ClientRetryPolicy(0), NetworkTimeout = TimeSpan.FromSeconds(30)
        });
    }
    public async Task<string?> StageAsync(string submissionKey, IReadOnlyList<BatchChatRequest> requests, CancellationToken token = default)
    {
        Key(submissionKey); Validate(requests);
        using var body = new MemoryStream();
        foreach (var request in requests) {
            var parameters = new Dictionary<string, object?> {
                ["model"] = model.Model, ["messages"] = new[] {new {role="system",content=request.System},new {role="user",content=request.Input}},
                ["max_completion_tokens"] = request.MaxOutputTokens, ["store"] = false
            };
            if (request.Schema is { } schema && model.Capabilities.StructuredOutput)
                parameters["response_format"] = new {type="json_schema",json_schema=new {name="document_enrichment",strict=true,schema}};
            if (request.Reasoning is { } reasoning && model.Capabilities.ReasoningControl) parameters["reasoning_effort"] = reasoning;
            var line = JsonSerializer.SerializeToUtf8Bytes(new {custom_id=request.Id,method="POST",url="/v1/chat/completions",body=parameters});
            body.Write(line); body.WriteByte(10);
        }
        body.Position=0;
        return await Call(async ct => {
            var file=await sdk.GetOpenAIFileClient().UploadFileAsync(body,"secondbrain-"+submissionKey+".jsonl",FileUploadPurpose.Batch,ct).ConfigureAwait(false);
            return Id(file.Value.Id);
        },token).ConfigureAwait(false);
    }
    public Task<string?> FindStagedAsync(string submissionKey,CancellationToken token=default)
    {
        Key(submissionKey);
        return Call<string?>(async ct=> {
            var response=await sdk.GetOpenAIFileClient().GetFilesAsync(FilePurpose.Batch,ct).ConfigureAwait(false);
            var files=response.Value.Where(file=>file.Filename=="secondbrain-"+submissionKey+".jsonl").ToArray();
            if(files.Length>1) throw Malformed();
            return files.Length==0?null:Id(files[0].Id);
        },token);
    }
    public async Task<BatchReceipt> SubmitAsync(string submissionKey, string? stagedFileId, IReadOnlyList<BatchChatRequest> requests, CancellationToken token = default)
    {
        Key(submissionKey); Validate(requests); var input=Id(stagedFileId);
        return await Call(async ct => {
            using var content=BinaryContent.Create(BinaryData.FromObjectAsJson(new {
                input_file_id=input,endpoint="/v1/chat/completions",completion_window="24h",
                metadata=new {secondbrain_submission=submissionKey},output_expires_after=new {anchor="created_at",seconds=86400}
            }));
            var operation=await sdk.GetBatchClient().CreateBatchAsync(content,false,new() {CancellationToken=ct}).ConfigureAwait(false);
            return new BatchReceipt(Id(operation.BatchId),input);
        },token).ConfigureAwait(false);
    }
    public Task<BatchReceipt?> FindAsync(string submissionKey, CancellationToken token = default)
    {
        Key(submissionKey);
        return Call<BatchReceipt?>(async ct => {
            BatchReceipt? found=null; var pages=0;
            await foreach(var page in sdk.GetBatchClient().GetBatchesAsync(null,100,new RequestOptions {CancellationToken=ct}).GetRawPagesAsync().WithCancellation(ct)) {
                if (++pages>10) throw Malformed();
                using var json=JsonDocument.Parse(page.GetRawResponse().Content);
                foreach(var entry in json.RootElement.GetProperty("data").EnumerateArray()) {
                    if(entry.TryGetProperty("metadata",out var metadata) && metadata.ValueKind==JsonValueKind.Object &&
                        metadata.TryGetProperty("secondbrain_submission",out var key) && key.GetString()==submissionKey) {
                        if(found is not null) throw Malformed();
                        found=new(Id(entry.GetProperty("id").GetString()),OptionalId(entry,"input_file_id"));
                    }
                }
                if(found is not null) return found;
            }
            return found;
        },token);
    }
    public Task<BatchSnapshot> PollAsync(BatchReceipt receipt, CancellationToken token = default)
    {
        Id(receipt.Id);
        return Call(async ct => {
            var response=await sdk.GetBatchClient().GetBatchAsync(receipt.Id,new RequestOptions {CancellationToken=ct}).ConfigureAwait(false);
            using var json=JsonDocument.Parse(response.GetRawResponse().Content);
            var root=json.RootElement;
            if(Id(root.GetProperty("id").GetString())!=receipt.Id) throw Malformed();
            var state=root.GetProperty("status").GetString();
            if(state is not ("validating" or "in_progress" or "finalizing" or "completed" or "failed" or "expired" or "cancelling" or "cancelled")) throw Malformed();
            var input=OptionalId(root,"input_file_id");
            if(receipt.InputFileId is not null && input!=receipt.InputFileId) throw Malformed();
            return new BatchSnapshot(new(receipt.Id,input),state,state is "completed" or "failed" or "expired" or "cancelled",OptionalId(root,"output_file_id"),OptionalId(root,"error_file_id"));
        },token);
    }
    public async Task CancelAsync(BatchReceipt receipt, CancellationToken token = default)
    {
        Id(receipt.Id);
        await Call(async ct => {
            var operation=await CreateBatchOperation.RehydrateAsync(sdk.GetBatchClient(),receipt.Id,ct).ConfigureAwait(false);
            await operation.CancelAsync(new() {CancellationToken=ct}).ConfigureAwait(false); return true;
        },token).ConfigureAwait(false);
    }
    public Task<IReadOnlyList<BatchChatResult>> CollectAsync(BatchSnapshot snapshot, IReadOnlyList<string> expectedIds, CancellationToken token = default)
    {
        if(!snapshot.Terminal || expectedIds.Count is <1 or >64 || expectedIds.Distinct(StringComparer.Ordinal).Count()!=expectedIds.Count) throw new ArgumentException("Invalid batch collection.");
        foreach(var id in expectedIds) Key(id);
        return Call<IReadOnlyList<BatchChatResult>>(async ct => {
            var results=new Dictionary<string,BatchChatResult>(StringComparer.Ordinal); var expected=expectedIds.ToHashSet(StringComparer.Ordinal);
            foreach(var file in new[]{snapshot.OutputFileId,snapshot.ErrorFileId}.Where(x=>x is not null).Distinct(StringComparer.Ordinal)) {
                var response=await sdk.GetOpenAIFileClient().DownloadFileAsync(Id(file),ct).ConfigureAwait(false);
                using var lines=new StringReader(new UTF8Encoding(false,true).GetString(response.Value.ToMemory().Span));
                string? line;var lineCount=0;
                while((line=lines.ReadLine()) is not null) {
                    if(++lineCount>128) throw Malformed();
                    if(string.IsNullOrWhiteSpace(line)) continue;
                    if(Encoding.UTF8.GetByteCount(line)>131072 || results.Count>=64) throw Malformed();
                    using var json=JsonDocument.Parse(line,new JsonDocumentOptions {MaxDepth=16}); var root=json.RootElement;
                    var id=Key(root.GetProperty("custom_id").GetString());
                    if(!expected.Contains(id) || results.ContainsKey(id)) throw Malformed();
                    results.Add(id,Result(root,id));
                }
            }
            return expectedIds.Select(id=>results.GetValueOrDefault(id) ?? new(id,null,null,"missing_result")).ToArray();
        },token);
    }
    public async Task DeleteFilesAsync(BatchSnapshot snapshot, CancellationToken token = default)
    {
        foreach(var id in new[]{snapshot.Receipt.InputFileId,snapshot.OutputFileId,snapshot.ErrorFileId}.Where(x=>x is not null).Distinct(StringComparer.Ordinal)) {
            try {await Call(async ct=> {await sdk.GetOpenAIFileClient().DeleteFileAsync(Id(id),ct).ConfigureAwait(false);return true;},token).ConfigureAwait(false);}
            catch(ProviderRequestException error) when(error.StatusCode==System.Net.HttpStatusCode.NotFound) { }
        }
    }
    private static BatchChatResult Result(JsonElement root,string id)
    {
        if(root.TryGetProperty("error",out var error) && error.ValueKind!=JsonValueKind.Null) return new(id,null,null,"request_failed");
        var response=root.GetProperty("response");
        if(response.GetProperty("status_code").GetInt32()!=200) return new(id,null,null,"request_failed");
        var body=response.GetProperty("body"); ModelUsageTotals? usage=null;
        if(body.TryGetProperty("usage",out var u) && u.ValueKind==JsonValueKind.Object) {
            static long? Count(JsonElement e,string name) => e.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetInt64(out var n)&&n>=0&&n<=100_000_000?n:null;
            var cached=u.TryGetProperty("prompt_tokens_details",out var details)&&details.ValueKind==JsonValueKind.Object?Count(details,"cached_tokens"):null;
            usage=new(Count(u,"prompt_tokens"),Count(u,"completion_tokens"),cached,Count(u,"total_tokens"));
        }
        var choices=body.GetProperty("choices"); if(choices.GetArrayLength()!=1) throw Malformed();
        var choice=choices[0]; if(choice.GetProperty("finish_reason").GetString()!="stop") return new(id,null,usage,"incomplete_result");
        var message=choice.GetProperty("message");
        if(message.TryGetProperty("tool_calls",out var tools)&&tools.ValueKind==JsonValueKind.Array&&tools.GetArrayLength()>0) return new(id,null,usage,"unexpected_tool");
        var text=message.GetProperty("content").GetString();
        if(text is null || Encoding.UTF8.GetByteCount(text)>32768) throw Malformed();
        return new(id,text,usage,null);
    }
    private void Validate(IReadOnlyList<BatchChatRequest> requests)
    {
        if(lifecycleOnly) throw new InvalidOperationException("A lifecycle binding cannot submit inference.");
        if(requests.Count is <1 or >64 || requests.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=requests.Count) throw new ArgumentException("Invalid batch size or ids.");
        foreach(var request in requests) {
            Key(request.Id);
            if(request.MaxOutputTokens is <128 or >2048 || request.MaxOutputTokens>model.Limits.MaxOutputTokens ||
                request.System is null || request.Input is null || Encoding.UTF8.GetByteCount(request.Input)>16000 || Encoding.UTF8.GetByteCount(request.System)>2048 ||
                Encoding.UTF8.GetByteCount(request.Input)+Encoding.UTF8.GetByteCount(request.System)+request.MaxOutputTokens+512>model.Limits.ContextTokens ||
                request.Reasoning is not (null or "low" or "medium" or "high") || request.Schema is { } schema && schema.GetRawText().Length>16384)
                throw new ArgumentException("Batch request exceeds its declared limits.");
        }
    }
    private static string Key(string? value) => value is {Length:>0 and <=100} && value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_')?value:throw new ArgumentException("Invalid batch identifier.");
    private static string Id(string? value) => value is {Length:>0 and <=200} && value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_')?value:throw Malformed();
    private static string? OptionalId(JsonElement root,string name) => root.TryGetProperty(name,out var value)&&value.ValueKind!=JsonValueKind.Null?Id(value.GetString()):null;
    private static ProviderRequestException Malformed()=>new("https://secondbrain.dev/problems/provider-malformed","Native batch returned invalid data.");
    private static async Task<T> Call<T>(Func<CancellationToken,Task<T>> action,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try {return await action(deadline.Token).ConfigureAwait(false);}
        catch(OperationCanceledException) when(!token.IsCancellationRequested) {throw new ProviderRequestException("https://secondbrain.dev/problems/provider-timeout","Native batch request timed out.",retryable:true);}
        catch(ClientResultException error) {
            var mapped=BoundChatClient.Translate(error);
            if(mapped is SecondBrain.Core.Privacy.PrivacyPolicyException privacy) throw privacy;
            if(mapped is ProviderRequestException problem) throw new ProviderRequestException(problem.ProblemType,problem.Message,problem.Retryable,problem.StatusCode);
            throw Malformed();
        }
        catch(Exception error) when(error is JsonException or InvalidOperationException or KeyNotFoundException or DecoderFallbackException) {throw Malformed();}
    }
    public void Dispose() {transport.Dispose();network.Dispose();}
}
