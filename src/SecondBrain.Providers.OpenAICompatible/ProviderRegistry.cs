using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;

namespace SecondBrain.Providers.OpenAICompatible;

/// <summary>Atomically replaces role clients on a valid reload. Existing clients still pass current policy on every send.</summary>
public sealed class ProviderRegistry : IProviderRegistry, IDisposable
{
    private readonly IPolicyHttpClientFactory factory;
    private readonly IProviderEgressPolicy privacy;
    private readonly ModelCatalog catalog;
    private readonly IProviderCredentialResolver credentials;
    private readonly TimeProvider timeProvider;
    private readonly IDisposable? subscription;
    private readonly IDisposable privacyValidationSubscription;
    private readonly object sync = new();
    private readonly List<OpenAICompatibleBinding> ownedBindings = [];
    private RegistrySnapshot snapshot;
    private readonly Dictionary<string, IProviderBinding> embeddingHistory = [];

    public ProviderRegistry(IOptionsMonitor<SecondBrainOptions> options, IPolicyHttpClientFactory factory,
        IProviderEgressPolicy privacy, ModelCatalog catalog, IProviderCredentialResolver credentials, TimeProvider? timeProvider = null)
    {
        this.factory = factory;
        this.privacy = privacy;
        this.catalog = catalog;
        this.credentials = credentials;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        snapshot = Build(options.CurrentValue);
        // Privacy installs its snapshot first in the monitor notification order. Register
        // catalog/credential validation there so an invalid provider reload cannot partially
        // publish new privacy pins before this registry rejects it.
        privacyValidationSubscription = privacy.RegisterConfigurationValidator(
            candidate => ValidateConfiguration(candidate, catalog, credentials));
        subscription = options.OnChange((candidate, _) =>
        {
            lock (sync) snapshot = Build(candidate);
        });
    }

    public IReadOnlyList<string> ProviderNames { get { lock (sync) return snapshot.Providers.Keys.ToArray(); } }
    public bool IsCurrentConfiguration(SecondBrainOptions options)
    {
        lock (sync) return ReferenceEquals(snapshot.Configuration, options);
    }
    public IProviderBinding GetProvider(string name)
    {
        lock (sync) return snapshot.Providers.TryGetValue(name, out var binding)
            ? binding : throw new InvalidOperationException("Configured provider could not be resolved.");
    }
    public IProviderBinding GetEmbeddingProvider(string name, string model, int dimensions, string? fingerprint = null)
    {
        lock (sync)
        {
            if (fingerprint is not null && embeddingHistory.TryGetValue(fingerprint, out var retained)) return retained;
            if (!snapshot.Configuration.Providers.TryGetValue(name, out var configured))
                throw new InvalidOperationException("Active embedding provider is no longer configured.");
            var bindingFingerprint = SecondBrain.Core.Ingestion.EmbeddingFingerprint.Create(name, configured.Endpoint, model, dimensions);
            if (fingerprint is not null && fingerprint != bindingFingerprint)
                throw new InvalidOperationException("Active embedding endpoint changed; keyword fallback is required.");
            if (embeddingHistory.TryGetValue(bindingFingerprint, out var existing)) return existing;
            var current = snapshot.Roles[ModelRole.Embed].Model.Limits;
            var limits = current with { EmbedDimensions = dimensions };
            var binding = new OpenAICompatibleBinding(name, new Uri(configured.Endpoint!), ModelRole.Embed,
                factory, privacy, catalog, credentials.Resolve(configured.ApiKeyReference), dimensions, timeProvider,
                limits, model);
            ownedBindings.Add(binding);
            embeddingHistory.Add(bindingFingerprint, binding);
            return binding;
        }
    }

    public ProviderRoleBinding GetRole(ModelRole role)
    {
        lock (sync) return snapshot.Roles.TryGetValue(role, out var binding)
            ? binding : throw new InvalidOperationException("Configured model role could not be resolved.");
    }

    public async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        RegistrySnapshot current;
        lock (sync) current = snapshot;
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (name, binding) in current.Providers)
            result.Add(name, await binding.ListModelsAsync(cancellationToken).ConfigureAwait(false));
        return result;
    }

    public async ValueTask<IReadOnlyDictionary<ModelRole, ProviderHealthResult>> TestAsync(CancellationToken cancellationToken = default)
    {
        RegistrySnapshot current;
        lock (sync) current = snapshot;
        var result = new Dictionary<ModelRole, ProviderHealthResult>();
        foreach (var (role, binding) in current.Roles)
            result.Add(role, await binding.Provider.HealthCheckAsync(cancellationToken).ConfigureAwait(false));
        return result;
    }

    /// <summary>Pure configuration/catalog validation usable by lane C before publishing an options reload.</summary>
    public static void ValidateConfiguration(SecondBrainOptions options, ModelCatalog catalog,
        IProviderCredentialResolver? credentials = null)
    {
        credentials ??= new EnvironmentProviderCredentialResolver();
        foreach (var provider in options.Providers.Values)
        {
            ValidateProvider(provider);
            _ = credentials.Resolve(provider.ApiKeyReference);
        }
        foreach (var role in new[] { ModelRole.Chat, ModelRole.Enrich, ModelRole.Embed, ModelRole.Rerank })
        {
            var binding = GetOptions(options.Models, role);
            if (binding is null && role == ModelRole.Rerank) continue;
            if (binding is null) throw new InvalidOperationException("Every required model role must be configured.");
            Validate(binding, role, new HashSet<ModelBindingOptions>(ReferenceEqualityComparer.Instance));
        }

        void Validate(ModelBindingOptions binding, ModelRole role, HashSet<ModelBindingOptions> visited)
        {
            if (!visited.Add(binding)) throw new InvalidOperationException("Provider fallback configuration contains a cycle.");
            if (!options.Providers.TryGetValue(binding.Provider, out var provider))
                throw new InvalidOperationException("Model role names an unknown provider.");
            ValidateProvider(provider);
            ModelCatalog.ValidateRole(role, catalog.Resolve(binding.Provider, binding.Model, EffectiveLimits(binding, role), binding.Capabilities));
            if (binding.Fallback is not null) Validate(binding.Fallback, role, visited);
        }
    }

    private static void ValidateProvider(ProviderOptions provider)
    {
        if (provider.Kind is not ("openai_compatible" or "openai-compatible"))
            throw new InvalidOperationException("Configured provider adapter is unavailable.");
        if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new InvalidOperationException("Configured provider requires an explicit HTTP(S) endpoint.");
    }

    private RegistrySnapshot Build(SecondBrainOptions options)
    {
        ValidateConfiguration(options, catalog, credentials);
        var created = new List<OpenAICompatibleBinding>();
        try
        {
            var roles = new Dictionary<ModelRole, ProviderRoleBinding>();
            var providers = new Dictionary<string, IProviderBinding>(StringComparer.Ordinal);
            foreach (var role in new[] { ModelRole.Chat, ModelRole.Enrich, ModelRole.Embed, ModelRole.Rerank })
            {
                var bindingOptions = GetOptions(options.Models, role);
                if (bindingOptions is null) continue;
                roles.Add(role, Create(bindingOptions, role));
            }
            foreach (var (name, provider) in options.Providers)
            {
                if (providers.ContainsKey(name)) continue;
                if (provider.Kind is not ("openai_compatible" or "openai-compatible"))
                    throw new InvalidOperationException("Configured provider adapter is unavailable.");
                var binding = NewBinding(name, provider, ModelRole.Enrich, null, null, null, null);
                providers.Add(name, binding);
            }
            foreach (var role in roles.Values.Where(role => role.Role == ModelRole.Embed))
                RememberEmbedding(role);
            ownedBindings.AddRange(created);
            return new(roles, providers, options);

            ProviderRoleBinding Create(ModelBindingOptions bindingOptions, ModelRole role)
            {
                var provider = options.Providers[bindingOptions.Provider];
                var resolved = catalog.Resolve(bindingOptions.Provider, bindingOptions.Model, EffectiveLimits(bindingOptions, role), bindingOptions.Capabilities);
                var binding = NewBinding(bindingOptions.Provider, provider, role,
                    role == ModelRole.Embed ? resolved.Limits.EmbedDimensions : null, resolved.Limits, resolved.Model, bindingOptions.Capabilities);
                providers.TryAdd(bindingOptions.Provider, binding);
                return new(role, binding, resolved, bindingOptions.Fallback is null ? null : Create(bindingOptions.Fallback, role));
            }

            OpenAICompatibleBinding NewBinding(string name, ProviderOptions provider, ModelRole role, int? dimensions, ModelLimits? limits,
                string? model, ModelCapabilityOptions? capabilities)
            {
                var binding = new OpenAICompatibleBinding(name, new Uri(provider.Endpoint ?? throw new InvalidOperationException("Provider endpoint is missing.")),
                    role, factory, privacy, catalog, credentials.Resolve(provider.ApiKeyReference), dimensions, timeProvider, limits, model, capabilities);
                created.Add(binding);
                return binding;
            }
        }
        catch
        {
            foreach (var binding in created) binding.Dispose();
            throw;
        }
    }

    private void RememberEmbedding(ProviderRoleBinding role)
    {
        if (role.Model.Limits.EmbedDimensions is { } dimensions)
            embeddingHistory[SecondBrain.Core.Ingestion.EmbeddingFingerprint.Create(role.Provider.ProviderName, role.Provider.Endpoint?.AbsoluteUri, role.Model.Model, dimensions)] = role.Provider;
        if (role.Fallback is not null) RememberEmbedding(role.Fallback);
    }

    private static ModelLimits EffectiveLimits(ModelBindingOptions binding, ModelRole role)
        => role == ModelRole.Embed && binding.Dimensions is not null
            ? binding.Limits with { EmbedDimensions = binding.Dimensions } : binding.Limits;

    private static ModelBindingOptions? GetOptions(ModelRolesOptions models, ModelRole role) => role switch
    {
        ModelRole.Chat => models.Chat, ModelRole.Enrich => models.Enrich,
        ModelRole.Embed => models.Embed, ModelRole.Rerank => models.Rerank,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public void Dispose()
    {
        subscription?.Dispose();
        privacyValidationSubscription.Dispose();
        lock (sync) foreach (var binding in ownedBindings) binding.Dispose();
    }

    private sealed record RegistrySnapshot(IReadOnlyDictionary<ModelRole, ProviderRoleBinding> Roles,
        IReadOnlyDictionary<string, IProviderBinding> Providers, SecondBrainOptions Configuration);
}
