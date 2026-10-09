using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Problems;
using SecondBrain.Core.Storage;
using SecondBrain.Storage;
using SecondBrain.Infrastructure.Security;
using SecondBrain.Core.Providers;
using SecondBrain.Infrastructure.Extraction;

namespace SecondBrain.Server.Http;

/// <summary>Other lanes register one contributor for each owned readiness component.</summary>
public interface IReadinessContributor
{
    string Name { get; }
    ValueTask<ReadinessStatus> CheckAsync(CancellationToken cancellationToken = default);
}

/// <param name="CheckedAt">Provider contributors must supply the last successful observation's UTC time.</param>
public sealed record ReadinessStatus(bool Ready, string? Detail = null, DateTimeOffset? CheckedAt = null);

public sealed class ReadinessService(IEnumerable<IReadinessContributor> contributors,
    IOptionsMonitor<SecondBrainOptions> options, TimeProvider clock)
{
    public async Task<IReadOnlyDictionary<string, ReadinessStatus>> CheckAsync(CancellationToken cancellationToken)
    {
        var required = new List<string> { "stores", "migrations", "lock", "canary", "extractor", "provider:chat", "provider:enrich", "provider:embed" };
        if (options.CurrentValue.Models.Rerank is not null) required.Add("provider:rerank");
        var registered = contributors.GroupBy(contributor => contributor.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var results = new Dictionary<string, ReadinessStatus>(StringComparer.Ordinal);
        foreach (var name in required.Concat(registered.Keys).Distinct(StringComparer.Ordinal))
        {
            if (!registered.TryGetValue(name, out var contributor))
            {
                results[name] = new(false, "Readiness contributor is not registered.");
                continue;
            }
            try
            {
                // A hung dependency must not indefinitely hold the readiness response.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var status = await contributor.CheckAsync(timeout.Token).AsTask().WaitAsync(timeout.Token);
                if (name.StartsWith("provider:", StringComparison.Ordinal) && status.Ready &&
                    (status.CheckedAt is null || status.CheckedAt > clock.GetUtcNow() ||
                     clock.GetUtcNow() - status.CheckedAt > TimeSpan.FromMinutes(5)))
                    status = new(false, "Provider observation is absent or older than five minutes.", status.CheckedAt);
                results[name] = status;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                results[name] = new(false, "Readiness check timed out.");
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                results[name] = new(false, "Readiness check failed.");
            }
        }
        return results;
    }

    public static async Task RespondAsync(HttpContext context, ReadinessService readiness)
    {
        var components = await readiness.CheckAsync(context.RequestAborted);
        if (components.Values.All(status => status.Ready))
        {
            await context.Response.WriteAsJsonAsync(new { ready = true, components }, context.RequestAborted);
            return;
        }
        await ProblemResponses.WriteAsync(context, 503, ProblemTypes.Capacity, "Daemon is not ready",
            extensions: new Dictionary<string, object?> { ["ready"] = false, ["components"] = components });
    }
}

public sealed class StoreReadinessContributor(IServiceProvider services) : IReadinessContributor
{
    public string Name => "stores";
    public async ValueTask<ReadinessStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var state = services.GetService<IStateStore>();
        var index = services.GetService<IIndexStore>();
        if (state is null || index is null) return new(false, "Both stores must be registered.");
        await using var stateLease = await state.OpenReadConnectionAsync(cancellationToken);
        await using var indexLease = await index.OpenReadConnectionAsync(cancellationToken);
        return new(stateLease.Connection.State == System.Data.ConnectionState.Open &&
            indexLease.Connection.State == System.Data.ConnectionState.Open);
    }
}

public sealed class CanaryReadinessContributor(IPrivacyReadiness privacy) : IReadinessContributor
{
    public string Name => "canary";
    public ValueTask<ReadinessStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var status = privacy.GetReadiness();
        return ValueTask.FromResult(new ReadinessStatus(status.IsReady,
            status.CanaryEnabled ? $"Egress canary: {status.CanaryState}. {status.Detail}" : status.Detail,
            status.LastCheckedAt));
    }
}

public sealed class MigrationReadinessContributor(IStorageStatus storage) : IReadinessContributor
{
    public string Name => "migrations";
    public ValueTask<ReadinessStatus> CheckAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ReadinessStatus(storage.Ready && storage.LastMigration is not null,
            storage.Ready ? "Migrations and durability recovery completed." : "Storage startup is incomplete."));
}

public sealed class LockReadinessContributor(DataRootLock rootLock) : IReadinessContributor
{
    public string Name => "lock";
    public ValueTask<ReadinessStatus> CheckAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ReadinessStatus(rootLock.IsHeld, "Exclusive data-root lock."));
}

public sealed class ExtractorReadinessContributor(IOptionsMonitor<SecondBrainOptions> options) : IReadinessContributor
{
    public string Name => "extractor";
    public async ValueTask<ReadinessStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var ready = await ExtractorPing.CheckAsync(options.CurrentValue.Extractor.SocketPath, cancellationToken);
        return new(ready, ready ? "Extractor answered ping." : "Extractor socket ping failed.");
    }
}

/// <summary>A single policy-owned role probe batch serves the contributors in one readiness request.</summary>
public sealed class ProviderReadinessProbe(IProviderRegistry providers, IOptionsMonitor<SecondBrainOptions> options, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyDictionary<ModelRole, ProviderHealthResult>? results;
    private SecondBrainOptions? observedOptions;
    private IReadOnlyDictionary<ModelRole, ProviderRoleBinding>? observedBindings;
    public async ValueTask<ReadinessStatus> CheckAsync(ModelRole role, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            if (!providers.IsCurrentConfiguration(options.CurrentValue))
                return new(false, "Provider configuration is still being validated.");
            if (results is null || !ReferenceEquals(observedOptions, options.CurrentValue) ||
                observedBindings is null || observedBindings.Any(binding => !ReferenceEquals(binding.Value, providers.GetRole(binding.Key))) ||
                results.Values.Any(result => result.CheckedAt > now ||
                    now - result.CheckedAt >= (result.IsHealthy ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(1))))
            {
                var candidate = options.CurrentValue;
                var bindings = new[] { ModelRole.Chat, ModelRole.Enrich, ModelRole.Embed }
                    .Concat(candidate.Models.Rerank is null ? [] : new[] { ModelRole.Rerank })
                    .ToDictionary(configuredRole => configuredRole, providers.GetRole);
                var checkedResults = await providers.TestAsync(cancellationToken);
                if (!ReferenceEquals(candidate, options.CurrentValue) || !providers.IsCurrentConfiguration(candidate) ||
                    bindings.Any(binding => !ReferenceEquals(binding.Value, providers.GetRole(binding.Key)) ||
                        !MatchesConfiguration(binding.Key, binding.Value, candidate)))
                {
                    results = null;
                    return new(false, "Provider configuration changed during its readiness observation.");
                }
                results = checkedResults;
                observedOptions = candidate;
                observedBindings = bindings;
            }
            return results.TryGetValue(role, out var status)
                ? new(status.IsHealthy, status.Detail, status.CheckedAt)
                : new(false, "Provider role is not configured.");
        }
        finally { gate.Release(); }
    }
    private static bool MatchesConfiguration(ModelRole role, ProviderRoleBinding binding, SecondBrainOptions options)
    {
        var configured = role switch
        {
            ModelRole.Chat => options.Models.Chat, ModelRole.Enrich => options.Models.Enrich,
            ModelRole.Embed => options.Models.Embed, ModelRole.Rerank => options.Models.Rerank,
            _ => null,
        };
        return configured is not null && configured.Provider == binding.Provider.ProviderName &&
            configured.Model == binding.Model.Model && options.Providers.TryGetValue(configured.Provider, out var provider) &&
            Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint) && endpoint == binding.Provider.Endpoint;
    }
    public void Dispose() => gate.Dispose();
}

public sealed class ProviderReadinessContributor(ModelRole role, ProviderReadinessProbe probe) : IReadinessContributor
{
    public string Name => "provider:" + role.ToString().ToLowerInvariant();
    public ValueTask<ReadinessStatus> CheckAsync(CancellationToken cancellationToken = default) => probe.CheckAsync(role, cancellationToken);
}
