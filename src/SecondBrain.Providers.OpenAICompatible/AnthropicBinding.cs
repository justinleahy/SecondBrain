using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;

namespace SecondBrain.Providers.OpenAICompatible;

/// <summary>Official Anthropic SDK with explicit credentials and policy-owned HTTP.</summary>
public sealed class AnthropicBinding : IProviderBinding, IDisposable
{
    private readonly ModelRole role;
    private readonly IProviderEgressPolicy privacy;
    private readonly ModelCatalog catalog;
    private readonly ModelLimits? limits;
    private readonly string? model;
    private readonly ModelCapabilityOptions? capabilities;
    private readonly TimeProvider clock;
    private readonly HttpClient network, transport, discovery;
    private readonly AnthropicClient sdk;
    private readonly ConcurrentDictionary<string, IChatClient> chats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AnthropicNativeBatch> batches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AnthropicNativeBatch> batchLifecycles = new(StringComparer.Ordinal);
    private readonly IPolicyHttpClientFactory factory;
    private readonly string? apiKey;

    public AnthropicBinding(string providerName, Uri endpoint, ModelRole role, IPolicyHttpClientFactory factory,
        IProviderEgressPolicy privacy, ModelCatalog catalog, string? apiKey = null,
        TimeProvider? clock = null, ModelLimits? limits = null, string? model = null, ModelCapabilityOptions? capabilities = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidOperationException("Provider endpoint must be an explicit HTTP(S) URL without credentials, query or fragment.");
        ProviderName = providerName; Endpoint = endpoint; this.role = role; this.privacy = privacy; this.catalog = catalog;
        this.clock = clock ?? TimeProvider.System; this.limits = limits; this.model = model; this.capabilities = capabilities;this.factory=factory;this.apiKey=apiKey;
        network = factory.CreateClient(role, this);
        transport = CreateTransport(network,apiKey);
        discovery = factory.CreateClient(role, this, discovery: true);
        // Explicit null credentials suppress the SDK's environment/profile credential chain.
        sdk = new AnthropicClient(new ClientOptions {
            HttpClient = transport, BaseUrl = endpoint.AbsoluteUri.TrimEnd('/'), ApiKey = apiKey,
            MaxRetries = 0, Timeout = TimeSpan.FromSeconds(30), OpenTelemetry = false
        });
        discovery.DefaultRequestHeaders.Add("anthropic-version", "2026-10-21");
        if (apiKey is not null) discovery.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
    }

    public string ProviderName { get; }
    public Uri Endpoint { get; }
    public Uri ModelDiscoveryEndpoint => new(Endpoint.AbsoluteUri.TrimEnd('/') + "/v1/models?limit=1000");
    public bool IsLocal => privacy.IsLocalEndpoint(Endpoint);
    public ValueTask<ResolvedModel> ResolveAsync(string requested, ModelLimits? overrides = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(catalog.Resolve(ProviderName, requested, overrides ?? limits, requested == model ? capabilities : null));
    }
    public IEmbeddingGenerator<string, Embedding<float>>? GetEmbeddingGenerator(string requested) => null;
    public IChatClient? GetChatClient(string requested)
    {
        if (role is ModelRole.Embed or ModelRole.Rerank) return null;
        return chats.GetOrAdd(requested, name => {
            if (model is not null && name != model) throw new InvalidOperationException("A role binding cannot override its configured model.");
            var resolved = catalog.Resolve(ProviderName, name, limits, name == model ? capabilities : null);
            ModelCatalog.ValidateRole(role, resolved);
            return new BoundChatClient(new NativeChat(sdk.AsIChatClient(name, resolved.Limits.MaxOutputTokens), resolved), name, resolved.Limits.MaxOutputTokens!.Value);
        });
    }
    public async ValueTask<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await discovery.GetAsync(ModelDiscoveryEndpoint, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > OpenAICompatibleBinding.MaxDiscoveryResponseBytes) throw OllamaBinding.Malformed();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var body = new MemoryStream(); var buffer = new byte[81920]; int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0) {
                if (body.Length + read > OpenAICompatibleBinding.MaxDiscoveryResponseBytes) throw OllamaBinding.Malformed();
                body.Write(buffer, 0, read);
            }
            using var json = JsonDocument.Parse(body.GetBuffer().AsMemory(0, checked((int)body.Length)));
            var data = json.RootElement.GetProperty("data");
            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 1000 ||
                json.RootElement.TryGetProperty("has_more", out var more) && more.GetBoolean()) throw OllamaBinding.Malformed();
            return data.EnumerateArray().Select(item => {
                var id = item.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id) || id.Length > OpenAICompatibleBinding.MaxModelIdLength) throw OllamaBinding.Malformed();
                return id;
            }).ToArray();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { throw OllamaBinding.Malformed(error); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw BoundChatClient.TimeoutFailure(error); }
        catch (IOException error) { throw Failure(error); }
    }
    public async ValueTask<ProviderHealthResult> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try { await ListModelsAsync(cancellationToken).ConfigureAwait(false); return new(true, clock.GetUtcNow()); }
        catch (Exception error) when (error is HttpRequestException or PrivacyPolicyException) {
            return new(false, clock.GetUtcNow(), error is ProviderRequestException request ? request.ProblemType : "Provider is unavailable.");
        }
    }
    internal static Exception Failure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is ProviderRequestException or PrivacyPolicyException) return current;
        // SDK exceptions can embed response bodies or credential-bearing request details.
        return error is IOException
            ? new ProviderRequestException("https://secondbrain.dev/problems/provider-unavailable", "Provider request failed.", retryable: true)
            : OllamaBinding.Malformed();
    }
    public void Dispose()
    {
        foreach (var chat in chats.Values) chat.Dispose();
        foreach(var batch in batches.Values) batch.Dispose();foreach(var batch in batchLifecycles.Values) batch.Dispose();
        sdk.Dispose(); transport.Dispose(); network.Dispose(); discovery.Dispose();
    }
    internal static HttpClient CreateTransport(HttpClient network,string? key)=>new(new NativeHeaders(key) {InnerHandler=new LimitedProviderHandler(network)}) {Timeout=TimeSpan.FromSeconds(30)};
    public IBatchProvider? GetBatchProvider(string requested)
    {
        if(role!=ModelRole.Enrich) return null;
        if(model is not null&&requested!=model) throw new InvalidOperationException("A role binding cannot override its configured model.");
        var resolved=catalog.Resolve(ProviderName,requested,limits,requested==model?capabilities:null);
        ModelCatalog.ValidateRole(role,resolved);
        return resolved.Capabilities.Batch?batches.GetOrAdd(requested,_=>new(this,factory,apiKey,resolved)):null;
    }
    public IBatchProvider GetBatchLifecycleProvider(string requested)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requested);
        return batchLifecycles.GetOrAdd(requested,_=>new(this,factory,apiKey,new(ProviderName,requested,new(),new()),true));
    }

    private sealed class NativeHeaders(string? key) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            // ParamsBase reads ANTHROPIC_CUSTOM_HEADERS globally. Do not forward ambient
            // authorization, routing, workspace, beta, or identifying metadata headers.
            request.Headers.Clear();
            request.Headers.TryAddWithoutValidation("anthropic-version", "2026-10-21");
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (key is not null) request.Headers.Authorization = new("Bearer", key);
            return base.SendAsync(request, token);
        }
    }

    private sealed class NativeChat(IChatClient inner, ResolvedModel model) : DelegatingChatClient(inner)
    {
        private ChatOptions Options(ChatOptions? options)
        {
            if (options?.AdditionalProperties?.Count > 0) throw new InvalidOperationException("Native chat accepts neutral options only.");
            var result = options?.Clone() ?? new(); result.ModelId = model.Model;
            if (!model.Capabilities.ReasoningControl) result.Reasoning = null;
            return result;
        }
        private List<ChatMessage> Messages(IEnumerable<ChatMessage> messages)
        {
            var result = messages.Select(message => new ChatMessage(message.Role, message.Contents.ToList()) { AuthorName = message.AuthorName }).ToList();
            if (model.Capabilities.PromptCaching)
            {
                // Clone the stable system block before marking it; never mutate shared caller history.
                var stable = result.TakeWhile(message => message.Role == ChatRole.System).LastOrDefault();
                if (stable?.Contents.LastOrDefault() is TextContent text)
                    stable.Contents[^1] = new TextContent(text.Text).WithCacheControl(new CacheControlEphemeral());
            }
            return result;
        }
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var input = Messages(messages); var bounded = Options(options);
            var citationSources=AnthropicCitations.Prepare(input,model.Capabilities.NativeCitations);
            if(citationSources.Length>0 && bounded.ResponseFormat is not null) throw new InvalidOperationException("Native citations cannot be combined with structured output.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try {
                var response = await InnerClient.GetResponseAsync(input, bounded, deadline.Token).ConfigureAwait(false);
                if (response.FinishReason is null) throw OllamaBinding.Malformed();
                if(model.Capabilities.NativeCitations) foreach(var message in response.Messages) message.Contents=AnthropicCitations.Expand(message.Contents,citationSources);
                return response;
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw BoundChatClient.TimeoutFailure(error); }
            catch (Exception error) when (error is not OperationCanceledException) { throw Failure(error); }
        }
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var input = Messages(messages); var bounded = Options(options);
            var citationSources=AnthropicCitations.Prepare(input,model.Capabilities.NativeCitations);
            if(citationSources.Length>0 && bounded.ResponseFormat is not null) throw new InvalidOperationException("Native citations cannot be combined with structured output.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await using var enumerator = InnerClient.GetStreamingResponseAsync(input, bounded, deadline.Token).GetAsyncEnumerator(deadline.Token);
            var stopped = false;
            while (true) {
                bool next;
                try { next = await enumerator.MoveNextAsync().ConfigureAwait(false); }
                catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw BoundChatClient.TimeoutFailure(error); }
                catch (Exception error) when (error is not OperationCanceledException) { throw Failure(error); }
                if (!next) { if (!stopped) throw OllamaBinding.Malformed(); yield break; }
                // A message_delta stop_reason alone does not mean the stream completed.
                if (enumerator.Current.RawRepresentation is RawMessageStreamEvent e && e.Value is RawMessageStopEvent) stopped = true;
                if(model.Capabilities.NativeCitations) enumerator.Current.Contents=AnthropicCitations.Expand(enumerator.Current.Contents,citationSources);
                yield return enumerator.Current;
            }
        }
    }
}
