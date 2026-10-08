using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Problems;
using SecondBrain.Core.Storage;

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

public sealed class CanaryReadinessContributor(IServiceProvider services, IOptionsMonitor<SecondBrainOptions> options) : IReadinessContributor
{
    public string Name => "canary";
    public ValueTask<ReadinessStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var privacy = services.GetService<IPrivacyPolicy>();
        return ValueTask.FromResult(!options.CurrentValue.Privacy.LocalOnly
            ? new ReadinessStatus(true)
            : new ReadinessStatus(privacy?.CanaryState == CanaryState.Blocked, "Local-only requires a blocked egress canary."));
    }
}
