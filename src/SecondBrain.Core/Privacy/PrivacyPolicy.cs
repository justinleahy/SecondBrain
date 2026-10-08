using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Providers;
using SecondBrain.Core.Problems;

namespace SecondBrain.Core.Privacy;

/// <summary>
/// Validates provider-data policy on construction, options reload, and every request.
/// Approved endpoint origins and DNS pins are immutable between successful reloads.
/// </summary>
public sealed class PrivacyPolicy : IPrivacyPolicy, IPrivacyReadiness, IDisposable
{
    public static readonly TimeSpan TrustedHostResolutionTimeout = TimeSpan.FromSeconds(10);
    private readonly IOptionsMonitor<SecondBrainOptions> _options;
    private readonly IDnsResolver _resolver;
    private readonly TimeProvider _timeProvider;
    private readonly IDisposable? _reloadSubscription;
    private readonly object _gate = new();
    private readonly Dictionary<long, Action<SecondBrainOptions>> _configurationValidators = [];
    private long _nextValidatorId;
    private PolicySnapshot _snapshot;
    private CanaryState _canaryState;
    private DateTimeOffset? _lastCheckedAt;
    private long _canaryGeneration;

    public PrivacyPolicy(
        IOptionsMonitor<SecondBrainOptions> options,
        IDnsResolver resolver,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolver);
        _options = options;
        _resolver = resolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _snapshot = Prepare(options.CurrentValue);
        _reloadSubscription = options.OnChange((candidate, _) =>
        {
            ValidateRegisteredConfiguration(candidate);
            var replacement = Prepare(candidate);
            lock (_gate)
            {
                if (_snapshot.CanaryTarget != replacement.CanaryTarget ||
                    _snapshot.CanaryEnabled != replacement.CanaryEnabled)
                {
                    _canaryState = CanaryState.Unknown;
                    _lastCheckedAt = null;
                    _canaryGeneration++;
                }

                _snapshot = replacement;
            }
        });
    }

    public CanaryState CanaryState
    {
        get { lock (_gate) { return _canaryState; } }
    }

    /// <summary>
    /// Validates a reload candidate without publishing it. Configuration loading calls this
    /// before changing IOptionsMonitor; its accepted OnChange notification installs the pins.
    /// </summary>
    public void ValidateConfiguration(SecondBrainOptions options) => _ = Prepare(options);

    /// <summary>
    /// Registers a collaborating component's pure candidate validator. All registered
    /// validators run before reload DNS resolution or snapshot publication; exceptions
    /// preserve the previous privacy state. The registry uses this for model validation.
    /// </summary>
    public IDisposable RegisterConfigurationValidator(Action<SecondBrainOptions> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        lock (_gate)
        {
            var id = ++_nextValidatorId;
            _configurationValidators.Add(id, validator);
            return new ConfigurationValidatorSubscription(this, id);
        }
    }

    /// <summary>Null denotes in-process execution; address classes never imply trust.</summary>
    public bool IsLocalEndpoint(Uri? endpoint)
    {
        lock (_gate)
        {
            return endpoint is null || _snapshot.Pins.ContainsKey(Origin(endpoint));
        }
    }

    public PrivacyDecision Evaluate(ModelRole role, IProviderBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        try
        {
            ValidateBinding(role, binding, false);
            return new PrivacyDecision(true);
        }
        catch (PrivacyPolicyException exception)
        {
            return new PrivacyDecision(false, exception.ProblemType, exception.Message);
        }
    }

    /// <summary>
    /// Re-checks the live role, binding, approved configuration, request origin and canary.
    /// Discovery may inspect a configured unbound provider, but never bypasses egress policy.
    /// </summary>
    public void ValidateRequest(ModelRole role, IProviderBinding binding, Uri requestUri, bool discovery = false)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        var endpoint = ValidateBinding(role, binding, discovery);
        if (endpoint is null || !SameOrigin(endpoint, requestUri))
        {
            throw Refusal("The provider request does not match its configured endpoint origin.");
        }
    }

    /// <summary>A defensive copy of the approved addresses for an exact trusted origin.</summary>
    public IReadOnlyList<IPAddress> GetPinnedAddresses(Uri endpoint)
    {
        lock (_gate)
        {
            EnsureCurrentConfiguration(_snapshot);
            return _snapshot.Pins.TryGetValue(Origin(endpoint), out var addresses)
                ? addresses.Select(CloneAddress).ToArray()
                : Array.Empty<IPAddress>();
        }
    }

    /// <summary>Checks DNS on every send, even when a pooled connection already exists.</summary>
    public void VerifyResolvedAddresses(Uri endpoint, IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        lock (_gate)
        {
            EnsureCurrentConfiguration(_snapshot);
            if (_snapshot.Pins.TryGetValue(Origin(endpoint), out var pinned))
            {
                VerifyPins(pinned, addresses);
            }
        }
    }

    /// <summary>
    /// Checks a ConnectCallback DNS result without relying on HTTP scheme being available.
    /// Every trusted origin for this host and port must approve every returned address.
    /// </summary>
    public void VerifyConnectionAddresses(string host, int port, IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        lock (_gate)
        {
            EnsureCurrentConfiguration(_snapshot);
            foreach (var origin in _snapshot.Pins.Keys)
            {
                if (origin.Port == port && string.Equals(origin.Host, host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
                {
                    VerifyPins(_snapshot.Pins[origin], addresses);
                }
            }
        }
    }

    public PrivacyReadiness GetReadiness()
    {
        lock (_gate)
        {
            try
            {
                EnsureCurrentConfiguration(_snapshot);
                CheckCanary(_snapshot);
                return new PrivacyReadiness(true, _snapshot.LocalOnly, _snapshot.CanaryEnabled,
                    _canaryState, _lastCheckedAt, null,
                    _snapshot.CanaryEnabled ? "Egress canary state is current." : "Egress canary is disabled by configuration.");
            }
            catch (PrivacyPolicyException exception)
            {
                return new PrivacyReadiness(false, _snapshot.LocalOnly, _snapshot.CanaryEnabled,
                    _canaryState, _lastCheckedAt, exception.ProblemType, exception.Message);
            }
        }
    }

    /// <summary>Captures the approved target and generation before an asynchronous check starts.</summary>
    public EgressCanaryConfiguration CaptureCanaryConfiguration()
    {
        lock (_gate)
        {
            EnsureCurrentConfiguration(_snapshot);
            return new EgressCanaryConfiguration(_snapshot.CanaryEnabled, _snapshot.CanaryTarget, _canaryGeneration);
        }
    }

    /// <summary>Publishes only a canary result from the still-current configuration generation.</summary>
    public void RecordCanaryResult(EgressCanaryConfiguration configuration, CanaryState state)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (state == CanaryState.Unknown)
        {
            throw new ArgumentException("A completed canary must be blocked or reachable.", nameof(state));
        }

        lock (_gate)
        {
            if (_snapshot.CanaryEnabled && configuration.Enabled && _canaryGeneration == configuration.Generation &&
                string.Equals(_snapshot.CanaryTarget, configuration.Target, StringComparison.Ordinal))
            {
                _canaryState = state;
                _lastCheckedAt = _timeProvider.GetUtcNow();
            }
        }
    }

    public void Dispose() => _reloadSubscription?.Dispose();

    private void ValidateRegisteredConfiguration(SecondBrainOptions options)
    {
        Action<SecondBrainOptions>[] validators;
        lock (_gate)
        {
            validators = _configurationValidators.Values.ToArray();
        }

        // Call outside the state lock: a pure collaborator may acquire its own lock.
        // ValidateConfiguration itself deliberately validates only privacy, preventing
        // recursive validation when the registry combines its check with that method.
        foreach (var validator in validators)
        {
            validator(options);
        }
    }

    private Uri? ValidateBinding(ModelRole role, IProviderBinding binding, bool discovery)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (_gate)
        {
            var snapshot = _snapshot;
            EnsureCurrentConfiguration(snapshot);
            if (!snapshot.Providers.TryGetValue(binding.ProviderName, out var configured))
            {
                throw Refusal("The provider binding is no longer configured.");
            }

            var endpoint = binding.Endpoint;
            if (configured.Endpoint != endpoint ||
                (endpoint is null && (!configured.InProcess || !binding.IsLocal)))
            {
                throw Refusal("The provider binding changed after policy approval.");
            }

            if (!discovery && !snapshot.Roles[role].Contains(binding.ProviderName))
            {
                throw Refusal("The provider is not bound to the requested model role.");
            }

            if (snapshot.LocalOnly && endpoint is not null && !snapshot.Pins.ContainsKey(Origin(endpoint)))
            {
                throw Refusal("local_only requires an in-process or exact trusted-service binding.");
            }

            CheckCanary(snapshot);
            return endpoint;
        }
    }

    private void EnsureCurrentConfiguration(PolicySnapshot snapshot)
    {
        if (!string.Equals(snapshot.Fingerprint, Fingerprint(_options.CurrentValue), StringComparison.Ordinal))
        {
            throw Refusal("Current provider configuration has not passed privacy policy validation.");
        }
    }

    private void CheckCanary(PolicySnapshot snapshot)
    {
        if (snapshot.LocalOnly && snapshot.CanaryEnabled && _canaryState != CanaryState.Blocked)
        {
            throw new PrivacyPolicyException(ProblemTypes.EgressUnverified,
                _canaryState == CanaryState.Reachable
                    ? "The public egress canary succeeded; the host egress rule is unverified."
                    : "The host egress rule has not yet been verified by the canary.");
        }
    }

    private PolicySnapshot Prepare(SecondBrainOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var fingerprint = Fingerprint(options);
        var pins = new Dictionary<EndpointOrigin, IPAddress[]>();
        foreach (var trusted in options.Privacy.TrustedServices)
        {
            if (!Uri.TryCreate(trusted, UriKind.Absolute, out var endpoint) ||
                !IsHttp(endpoint) || endpoint.AbsolutePath != "/" ||
                endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0)
            {
                throw Refusal("A trusted service must be an exact HTTP(S) scheme, host and port origin.");
            }

            IPAddress[] addresses;
            try
            {
                using var timeout = new CancellationTokenSource(TrustedHostResolutionTimeout, _timeProvider);
                addresses = _resolver.ResolveAsync(endpoint.IdnHost, timeout.Token).AsTask()
                    .WaitAsync(TrustedHostResolutionTimeout, _timeProvider).GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is not PrivacyPolicyException)
            {
                throw new PrivacyPolicyException(ProblemTypes.PrivacyPolicy, "A trusted-service hostname could not be resolved.", exception);
            }

            if (addresses.Length == 0)
            {
                throw Refusal("A trusted-service hostname resolved to no addresses.");
            }

            var origin = Origin(endpoint);
            var distinct = addresses.Distinct().Select(CloneAddress).ToArray();
            if (pins.TryGetValue(origin, out var existing) && !existing.ToHashSet().SetEquals(distinct))
            {
                throw Refusal("A duplicate trusted service resolved to inconsistent DNS addresses.");
            }

            pins[origin] = distinct;
        }

        var providers = new Dictionary<string, ProviderSnapshot>(StringComparer.Ordinal);
        foreach (var (name, provider) in options.Providers)
        {
            var inProcess = string.Equals(provider.Kind, "in_process", StringComparison.OrdinalIgnoreCase);
            Uri? endpoint = null;
            if (!string.IsNullOrWhiteSpace(provider.Endpoint))
            {
                if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out endpoint) || !IsHttp(endpoint) ||
                    endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                {
                    throw Refusal("A provider endpoint must be an absolute HTTP(S) URL without credentials, query or fragment.");
                }
            }
            else if (!inProcess)
            {
                throw Refusal("A network provider must have an explicit endpoint.");
            }

            if (inProcess && endpoint is not null)
            {
                throw Refusal("An in-process provider cannot declare a network endpoint.");
            }

            providers[name] = new ProviderSnapshot(endpoint, inProcess);
        }

        var roles = new Dictionary<ModelRole, HashSet<string>>();
        foreach (var (role, binding) in EnumerateRoles(options.Models))
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<ModelBindingOptions>(ReferenceEqualityComparer.Instance);
            for (var current = binding; current is not null; current = current.Fallback)
            {
                if (!visited.Add(current))
                {
                    throw Refusal("A provider fallback chain cannot contain a cycle.");
                }

                if (!providers.TryGetValue(current.Provider, out var provider))
                {
                    throw Refusal("A model role references an unresolved provider binding.");
                }

                if (options.Privacy.LocalOnly && !provider.InProcess &&
                    (provider.Endpoint is null || !pins.ContainsKey(Origin(provider.Endpoint))))
                {
                    throw Refusal("local_only refuses a hosted model role or fallback binding.");
                }

                names.Add(current.Provider);
            }

            roles[role] = names;
        }

        _ = EgressCanary.ParseTarget(options.Privacy.CanaryTarget);
        if (!string.Equals(fingerprint, Fingerprint(options), StringComparison.Ordinal))
        {
            throw Refusal("Provider configuration changed while privacy validation was running.");
        }

        return new PolicySnapshot(fingerprint, options.Privacy.LocalOnly, options.Privacy.EgressCanary,
            options.Privacy.CanaryTarget, pins, providers, roles);
    }

    private static IEnumerable<(ModelRole Role, ModelBindingOptions? Binding)> EnumerateRoles(ModelRolesOptions models)
    {
        yield return (ModelRole.Chat, models.Chat);
        yield return (ModelRole.Enrich, models.Enrich);
        yield return (ModelRole.Embed, models.Embed);
        yield return (ModelRole.Rerank, models.Rerank);
    }

    private static string Fingerprint(SecondBrainOptions options)
    {
        try
        {
            // Secrets, bodies and unrelated config never enter the fingerprint or diagnostics.
            var value = new
            {
                options.Privacy.LocalOnly,
                options.Privacy.EgressCanary,
                options.Privacy.CanaryTarget,
                Trusted = options.Privacy.TrustedServices.Order(StringComparer.Ordinal).ToArray(),
                Providers = options.Providers.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => new { entry.Key, entry.Value.Kind, entry.Value.Endpoint }).ToArray(),
                Roles = EnumerateRoles(options.Models).Select(entry => new
                {
                    entry.Role,
                    Bindings = Flatten(entry.Binding),
                }).ToArray(),
            };
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
        }
        catch (InvalidOperationException exception)
        {
            throw new PrivacyPolicyException(ProblemTypes.PrivacyPolicy, "Provider configuration changed during policy evaluation.", exception);
        }
    }

    private static object[] Flatten(ModelBindingOptions? binding)
    {
        var values = new List<object>();
        var visited = new HashSet<ModelBindingOptions>(ReferenceEqualityComparer.Instance);
        for (var current = binding; current is not null; current = current.Fallback)
        {
            if (!visited.Add(current))
            {
                throw Refusal("A provider fallback chain cannot contain a cycle.");
            }

            values.Add(new { current.Provider, current.Model });
        }

        return values.ToArray();
    }

    private static void VerifyPins(IPAddress[] pinned, IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0 || addresses.Any(address => !pinned.Contains(address)))
        {
            throw Refusal("A trusted-service DNS result differs from its configuration-time address pins.");
        }
    }

    private static bool IsHttp(Uri uri) => uri.Scheme is "http" or "https";

    private static EndpointOrigin Origin(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !IsHttp(uri))
        {
            throw Refusal("Provider-data egress requires an absolute HTTP(S) endpoint.");
        }

        return new EndpointOrigin(uri.Scheme.ToLowerInvariant(), uri.IdnHost.Trim('[', ']').ToLowerInvariant(), uri.Port);
    }

    private static bool SameOrigin(Uri first, Uri second) => Origin(first) == Origin(second);
    private static IPAddress CloneAddress(IPAddress address) => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
        ? new IPAddress(address.GetAddressBytes(), address.ScopeId)
        : new IPAddress(address.GetAddressBytes());
    private static PrivacyPolicyException Refusal(string message) => new(ProblemTypes.PrivacyPolicy, message);

    private sealed record EndpointOrigin(string Scheme, string Host, int Port);
    private sealed record ProviderSnapshot(Uri? Endpoint, bool InProcess);
    private sealed record PolicySnapshot(
        string Fingerprint,
        bool LocalOnly,
        bool CanaryEnabled,
        string CanaryTarget,
        Dictionary<EndpointOrigin, IPAddress[]> Pins,
        Dictionary<string, ProviderSnapshot> Providers,
        Dictionary<ModelRole, HashSet<string>> Roles);

    private sealed class ConfigurationValidatorSubscription(PrivacyPolicy owner, long id) : IDisposable
    {
        private PrivacyPolicy? _owner = owner;

        public void Dispose()
        {
            var policy = Interlocked.Exchange(ref _owner, null);
            if (policy is not null)
            {
                lock (policy._gate)
                {
                    policy._configurationValidators.Remove(id);
                }
            }
        }
    }
}
