using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Exceptions;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;

namespace SecondBrain.Providers.OpenAICompatible;

/// <summary>Native Ollama API using the same policy-owned transport as every other provider.</summary>
public sealed class OllamaBinding : IProviderBinding, IDisposable
{
    private readonly ModelRole role;
    private readonly IProviderEgressPolicy privacy;
    private readonly ModelCatalog catalog;
    private readonly ModelLimits? limits;
    private readonly string? model;
    private readonly ModelCapabilityOptions? capabilities;
    private readonly int? dimensions;
    private readonly TimeProvider clock;
    private readonly HttpClient network, sdkTransport, discovery;
    private readonly ConcurrentDictionary<string, OllamaApiClient> clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IChatClient> chats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IEmbeddingGenerator<string, Embedding<float>>> embeds = new(StringComparer.Ordinal);

    public OllamaBinding(string providerName, Uri endpoint, ModelRole role, IPolicyHttpClientFactory factory,
        IProviderEgressPolicy privacy, ModelCatalog catalog, string? apiKey = null, int? dimensions = null,
        TimeProvider? clock = null, ModelLimits? limits = null, string? model = null, ModelCapabilityOptions? capabilities = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidOperationException("Provider endpoint must be an explicit HTTP(S) URL without credentials, query or fragment.");
        ProviderName = providerName; Endpoint = endpoint; this.role = role; this.privacy = privacy; this.catalog = catalog;
        this.dimensions = dimensions; this.clock = clock ?? TimeProvider.System; this.limits = limits; this.model = model; this.capabilities = capabilities;
        network = factory.CreateClient(role, this);
        sdkTransport = new HttpClient(new LimitedProviderHandler(network)) { BaseAddress = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        discovery = factory.CreateClient(role, this, discovery: true);
        if (apiKey is not null) { sdkTransport.DefaultRequestHeaders.Authorization = new("Bearer", apiKey); discovery.DefaultRequestHeaders.Authorization = new("Bearer", apiKey); }
    }
    public string ProviderName { get; }
    public Uri Endpoint { get; }
    public Uri ModelDiscoveryEndpoint => new(Endpoint.AbsoluteUri.TrimEnd('/') + "/api/tags");
    public string EmbeddingIdentity => Endpoint.AbsoluteUri + "|ollama-native-v1";
    public bool IsLocal => privacy.IsLocalEndpoint(Endpoint);
    public ValueTask<ResolvedModel> ResolveAsync(string requested, ModelLimits? overrides = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(catalog.Resolve(ProviderName, requested, overrides ?? limits, requested == model ? capabilities : null));
    }
    private ResolvedModel Resolve(string requested)
    {
        if (model is not null && requested != model) throw new InvalidOperationException("A role binding cannot override its configured model.");
        var result = catalog.Resolve(ProviderName, requested, limits, requested == model ? capabilities : null);
        ModelCatalog.ValidateRole(role, result); return result;
    }
    private OllamaApiClient Client(string requested) => clients.GetOrAdd(requested, name => new OllamaApiClient(sdkTransport, name));
    public IChatClient? GetChatClient(string requested) => role == ModelRole.Embed ? null : chats.GetOrAdd(requested, name =>
    {
        var resolved = Resolve(name);
        return new BoundChatClient(new NativeChat(Client(name), resolved), name, resolved.Limits.MaxOutputTokens!.Value);
    });
    public IEmbeddingGenerator<string, Embedding<float>>? GetEmbeddingGenerator(string requested) => role != ModelRole.Embed ? null : embeds.GetOrAdd(requested, name =>
    {
        var resolved = Resolve(name);
        return new BoundEmbeddingGenerator(new NativeEmbedding(Client(name), name, Endpoint, dimensions ?? resolved.Limits.EmbedDimensions!.Value),
            name, dimensions ?? resolved.Limits.EmbedDimensions, resolved.Limits.EmbedBatchMax!.Value);
    });

    public async ValueTask<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await discovery.GetAsync(ModelDiscoveryEndpoint, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > OpenAICompatibleBinding.MaxDiscoveryResponseBytes) throw Malformed();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var content = new MemoryStream(); var buffer = new byte[81920]; int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (content.Length + read > OpenAICompatibleBinding.MaxDiscoveryResponseBytes) throw Malformed();
                content.Write(buffer, 0, read);
            }
            using var json = JsonDocument.Parse(content.GetBuffer().AsMemory(0, checked((int)content.Length)));
            var values = json.RootElement.GetProperty("models");
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > OpenAICompatibleBinding.MaxDiscoveredModels) throw Malformed();
            return values.EnumerateArray().Select(value =>
            {
                var name = value.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(name) || name.Length > OpenAICompatibleBinding.MaxModelIdLength) throw Malformed();
                return name;
            }).ToArray();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { throw Malformed(error); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw BoundChatClient.TimeoutFailure(error); }
        catch (IOException error) { throw Unavailable(error); }
    }
    public async ValueTask<ProviderHealthResult> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try { await ListModelsAsync(cancellationToken).ConfigureAwait(false); return new(true, clock.GetUtcNow()); }
        catch (Exception error) when (error is HttpRequestException or PrivacyPolicyException)
        { return new(false, clock.GetUtcNow(), error is ProviderRequestException request ? request.ProblemType : "Provider is unavailable."); }
    }
    internal static ProviderRequestException Malformed(Exception? error = null) => new("https://secondbrain.dev/problems/provider-malformed", "Native provider returned invalid data.", innerException: error);
    private static ProviderRequestException Unavailable(Exception error) => new("https://secondbrain.dev/problems/provider-unavailable", "Native provider request failed.", retryable: true, innerException: error);
    public void Dispose()
    {
        foreach (var value in chats.Values) value.Dispose(); foreach (var value in embeds.Values) value.Dispose();
        foreach (var value in clients.Values) value.Dispose(); sdkTransport.Dispose(); network.Dispose(); discovery.Dispose();
    }

    private sealed class NativeChat(OllamaApiClient client, ResolvedModel resolved) : DelegatingChatClient(client)
    {
        private ChatOptions Options(ChatOptions? value)
        {
            if (value?.AdditionalProperties?.Count > 0) throw new InvalidOperationException("Native chat accepts neutral options only.");
            var result = value?.Clone() ?? new(); result.ModelId = resolved.Model;
            result.AdditionalProperties = new() { ["num_ctx"] = resolved.Limits.ContextTokens!.Value };
            if (!resolved.Capabilities.ReasoningControl) result.Reasoning = null;
            return result;
        }
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var requestOptions = Options(options);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try { return await InnerClient.GetResponseAsync(messages, requestOptions, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw BoundChatClient.TimeoutFailure(error); }
            catch (Exception error) when (error is JsonException or OllamaException) { throw Malformed(error); }
            catch (InvalidOperationException error) { throw Malformed(error); }
            catch (IOException error) { throw Unavailable(error); }
        }
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await using var enumerator = InnerClient.GetStreamingResponseAsync(messages, Options(options), deadline.Token).GetAsyncEnumerator(deadline.Token);
            var finished = false;
            while (true)
            {
                bool next;
                try { next = await enumerator.MoveNextAsync().ConfigureAwait(false); }
                catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw BoundChatClient.TimeoutFailure(error); }
                catch (Exception error) when (error is JsonException or OllamaException) { throw Malformed(error); }
                catch (InvalidOperationException error) { throw Malformed(error); }
                catch (IOException error) { throw Unavailable(error); }
                if (!next) { if (!finished) throw Malformed(); yield break; }
                finished |= enumerator.Current.FinishReason is not null;
                yield return enumerator.Current;
            }
        }
    }

    private sealed class NativeEmbedding(OllamaApiClient client, string model, Uri endpoint, int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (options?.AdditionalProperties?.Any(pair => pair.Key != "intent" || pair.Value is not ("query" or "document")) == true)
                throw new InvalidOperationException("Native embeddings accept neutral options only.");
            var inputs = values.ToArray();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var response = await client.EmbedAsync(new EmbedRequest { Model = model, Input = inputs.ToList(), Dimensions = dimensions, Truncate = false }, deadline.Token).ConfigureAwait(false);
                if (response.Embeddings is null || response.Embeddings.Count != inputs.Length) throw Malformed();
                var result = new GeneratedEmbeddings<Embedding<float>>(response.Embeddings.Select(vector =>
                {
                    var numbers = vector.ToArray();
                    if (numbers.Length != dimensions || numbers.Any(number => !float.IsFinite(number))) throw Malformed();
                    return new Embedding<float>(numbers) { ModelId = model };
                }));
                if (response.PromptEvalCount > 0) result.Usage = new() { InputTokenCount = response.PromptEvalCount, OutputTokenCount = 0, TotalTokenCount = response.PromptEvalCount };
                return result;
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw BoundChatClient.TimeoutFailure(error); }
            catch (Exception error) when (error is JsonException or OllamaException) { throw Malformed(error); }
            catch (IOException error) { throw Unavailable(error); }
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType == typeof(EmbeddingGeneratorMetadata)
            ? new EmbeddingGeneratorMetadata("ollama", endpoint, model, dimensions) : null;
        public void Dispose() { }
    }
}

internal sealed class LimitedProviderHandler(HttpClient inner) : HttpMessageHandler
{
    private const int Limit = 32 * 1024 * 1024;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri) { Content = request.Content };
        foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options) forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        HttpResponseMessage response;
        try { response = await inner.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        finally { forwarded.Content = null; }
        if (response.Content.Headers.ContentLength > Limit) { response.Dispose(); throw OllamaBinding.Malformed(); }
        response.Content = new LimitedContent(response.Content); return response;
    }
    private sealed class LimitedContent : HttpContent
    {
        private readonly HttpContent inner;
        public LimitedContent(HttpContent inner) { this.inner = inner; foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value); }
        protected override async Task<Stream> CreateContentReadStreamAsync() => new LimitedStream(await inner.ReadAsStreamAsync().ConfigureAwait(false));
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => new LimitedStream(await inner.ReadAsStreamAsync(token).ConfigureAwait(false));
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { await using var source = await CreateContentReadStreamAsync().ConfigureAwait(false); await source.CopyToAsync(stream).ConfigureAwait(false); }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        { await using var source = await CreateContentReadStreamAsync(token).ConfigureAwait(false); await source.CopyToAsync(stream, token).ConfigureAwait(false); }
        protected override bool TryComputeLength(out long length) { length = inner.Headers.ContentLength ?? 0; return inner.Headers.ContentLength.HasValue; }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class LimitedStream(Stream inner) : Stream
    {
        private long consumed;
        private int Count(int count) { consumed += count; if (consumed > Limit) throw OllamaBinding.Malformed(); return count; }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
