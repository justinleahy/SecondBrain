using System.Collections.Concurrent;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;

namespace SecondBrain.Providers.OpenAICompatible;

/// <summary>A configured compatible endpoint. No vendor endpoint or model is selected implicitly.</summary>
public sealed class OpenAICompatibleBinding : IProviderBinding, IDisposable
{
    private readonly IProviderEgressPolicy privacy;
    private readonly ModelCatalog catalog;
    private readonly ModelRole role;
    private readonly HttpClient inferenceClient;
    private readonly HttpClient discoveryClient;
    private readonly OpenAIClient sdk;
    private readonly TimeProvider timeProvider;
    private readonly ModelLimits? configuredLimits;
    private readonly string? configuredModel;
    private readonly ConcurrentDictionary<string, ResolvedModel> resolvedModels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IChatClient> chatClients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IEmbeddingGenerator<string, Embedding<float>>> embeddingClients = new(StringComparer.Ordinal);

    public OpenAICompatibleBinding(string providerName, Uri endpoint, ModelRole role,
        IPolicyHttpClientFactory factory, IProviderEgressPolicy privacy, ModelCatalog catalog,
        string? apiKey = null, int? dimensions = null, TimeProvider? timeProvider = null,
        ModelLimits? configuredLimits = null, string? configuredModel = null)
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
        var resolved = catalog.Resolve(ProviderName, model, limitsOverrides ?? configuredLimits);
        resolvedModels[model] = resolved;
        return ValueTask.FromResult(resolved);
    }

    public async ValueTask<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await discoveryClient.GetAsync(new Uri(Endpoint.AbsoluteUri.TrimEnd('/') + "/models"), cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            return json.RootElement.GetProperty("data").EnumerateArray()
                .Select(model => model.GetProperty("id").GetString() ?? throw new JsonException()).ToArray();
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
    }

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
        var resolved = resolvedModels.GetOrAdd(model, key => catalog.Resolve(ProviderName, key, configuredLimits));
        ModelCatalog.ValidateRole(role, resolved);
        return resolved;
    }

    public void Dispose()
    {
        foreach (var client in chatClients.Values) client.Dispose();
        foreach (var client in embeddingClients.Values) client.Dispose();
        inferenceClient.Dispose();
        discoveryClient.Dispose();
    }
}
