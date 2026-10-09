using System.Collections.Concurrent;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;

namespace SecondBrain.Providers.OpenAICompatible;

/// <summary>A configured compatible endpoint. No vendor endpoint or model is selected implicitly.</summary>
public sealed class OpenAICompatibleBinding : IProviderBinding, IDisposable
{
    /// <summary>The largest /models response body read; larger bodies are malformed provider output.</summary>
    public const int MaxDiscoveryResponseBytes = 8 * 1024 * 1024;
    /// <summary>The most model entries accepted from one /models response.</summary>
    public const int MaxDiscoveredModels = 10_000;
    /// <summary>The longest model identifier accepted from discovery.</summary>
    public const int MaxModelIdLength = 512;

    private readonly IProviderEgressPolicy privacy;
    private readonly ModelCatalog catalog;
    private readonly ModelRole role;
    private readonly HttpClient inferenceClient;
    private readonly HttpClient discoveryClient;
    private readonly OpenAIClient sdk;
    private readonly TimeProvider timeProvider;
    private readonly ModelLimits? configuredLimits;
    private readonly string? configuredModel;
    private readonly ModelCapabilityOptions? configuredCapabilities;
    private readonly ConcurrentDictionary<string, ResolvedModel> resolvedModels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IChatClient> chatClients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IEmbeddingGenerator<string, Embedding<float>>> embeddingClients = new(StringComparer.Ordinal);

    public OpenAICompatibleBinding(string providerName, Uri endpoint, ModelRole role,
        IPolicyHttpClientFactory factory, IProviderEgressPolicy privacy, ModelCatalog catalog,
        string? apiKey = null, int? dimensions = null, TimeProvider? timeProvider = null,
        ModelLimits? configuredLimits = null, string? configuredModel = null,
        ModelCapabilityOptions? configuredCapabilities = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new InvalidOperationException("Provider endpoint must be an absolute HTTP(S) URL without credentials, query or fragment.");
        ProviderName = providerName;
        Endpoint = endpoint;
        this.role = role;
        this.privacy = privacy;
        this.catalog = catalog;
        Dimensions = dimensions;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.configuredLimits = configuredLimits;
        this.configuredModel = configuredModel;
        this.configuredCapabilities = configuredCapabilities;
        inferenceClient = factory.CreateClient(role, this);
        discoveryClient = factory.CreateClient(role, this, discovery: true);
        // The SDK has no independent HTTP transport or retry loop. Every send crosses our factory.
        sdk = new OpenAIClient(new ApiKeyCredential(apiKey ?? "no-key-configured"), new OpenAIClientOptions
        {
            Endpoint = endpoint,
            Transport = new HttpClientPipelineTransport(inferenceClient),
            RetryPolicy = new ClientRetryPolicy(0),
            NetworkTimeout = TimeSpan.FromSeconds(30),
        });
        if (apiKey is not null)
            discoveryClient.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
    }

    public string ProviderName { get; }
    public Uri Endpoint { get; }
    public bool IsLocal => privacy.IsLocalEndpoint(Endpoint);
    public int? Dimensions { get; }

    public ValueTask<ResolvedModel> ResolveAsync(string model, ModelLimits? limitsOverrides = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = catalog.Resolve(ProviderName, model, limitsOverrides ?? configuredLimits, DeclaredCapabilities(model));
        resolvedModels[model] = resolved;
        return ValueTask.FromResult(resolved);
    }

    public async ValueTask<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        // Response headers end HttpClient.Timeout's coverage, so the body read shares one deadline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(discoveryClient.Timeout);
        try
        {
            using var response = await discoveryClient.GetAsync(new Uri(Endpoint.AbsoluteUri.TrimEnd('/') + "/models"),
                HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MaxDiscoveryResponseBytes)
                throw OversizedModelList();
            var body = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);
            var data = json.RootElement.GetProperty("data");
            if (data.GetArrayLength() > MaxDiscoveredModels) throw OversizedModelList();
            var models = new List<string>(data.GetArrayLength());
            foreach (var model in data.EnumerateArray())
            {
                var id = model.GetProperty("id").GetString() ?? throw new JsonException();
                if (id.Length > MaxModelIdLength) throw OversizedModelList();
                models.Add(id);
            }
            return models;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ProviderRequestException("https://secondbrain.dev/problems/provider-malformed",
                "Provider returned an invalid model list.", innerException: exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderRequestException("https://secondbrain.dev/problems/provider-timeout",
                "Provider request timed out.", retryable: true, innerException: exception);
        }
        catch (IOException exception)
        {
            throw new ProviderRequestException("https://secondbrain.dev/problems/provider-unavailable",
                "Provider connection failed.", retryable: true, innerException: exception);
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxDiscoveryResponseBytes) throw OversizedModelList();
            buffer.Write(chunk, 0, read);
        }
        return buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
    }

    private static ProviderRequestException OversizedModelList()
        => new("https://secondbrain.dev/problems/provider-malformed", "Provider model list exceeds discovery limits.");

    public async ValueTask<ProviderHealthResult> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ListModelsAsync(cancellationToken).ConfigureAwait(false);
            return new(true, timeProvider.GetUtcNow());
        }
        catch (Exception exception) when (exception is HttpRequestException or PrivacyPolicyException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(false, timeProvider.GetUtcNow(), exception is PrivacyPolicyException privacyFailure
                ? privacyFailure.ProblemType : exception is ProviderRequestException providerFailure ? providerFailure.ProblemType : "Provider request timed out.");
        }
    }

    public IChatClient? GetChatClient(string model)
        => role == ModelRole.Embed ? null : chatClients.GetOrAdd(model,
            key =>
            {
                var resolved = ResolveForClient(key);
                return new BoundChatClient(sdk.GetChatClient(key).AsIChatClient(), key, resolved.Limits.MaxOutputTokens!.Value);
            });

    public IEmbeddingGenerator<string, Embedding<float>>? GetEmbeddingGenerator(string model)
        => role != ModelRole.Embed ? null : embeddingClients.GetOrAdd(model,
            key =>
            {
                var resolved = ResolveForClient(key);
                return new BoundEmbeddingGenerator(sdk.GetEmbeddingClient(key).AsIEmbeddingGenerator(Dimensions), key,
                    Dimensions ?? resolved.Limits.EmbedDimensions, resolved.Limits.EmbedBatchMax!.Value);
            });

    private ResolvedModel ResolveForClient(string model)
    {
        if (configuredModel is not null && configuredModel != model)
            throw new InvalidOperationException("A role binding cannot override its configured model.");
        var resolved = resolvedModels.GetOrAdd(model, key => catalog.Resolve(ProviderName, key, configuredLimits, DeclaredCapabilities(key)));
        ModelCatalog.ValidateRole(role, resolved);
        return resolved;
    }

    // Capability declarations describe the configured concrete model only.
    private ModelCapabilityOptions? DeclaredCapabilities(string model) => model == configuredModel ? configuredCapabilities : null;

    public void Dispose()
    {
        foreach (var client in chatClients.Values) client.Dispose();
        foreach (var client in embeddingClients.Values) client.Dispose();
        inferenceClient.Dispose();
        discoveryClient.Dispose();
    }
}
