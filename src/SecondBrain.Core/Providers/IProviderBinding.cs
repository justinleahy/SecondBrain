using Microsoft.Extensions.AI;

namespace SecondBrain.Core.Providers;

/// <summary>
/// A named provider connection exposing only neutral AI abstractions.
/// Every network operation, including discovery and health checks, must use the
/// policy-owned transport (spec §§13.2–13.5, 15.6, SEC-15).
/// </summary>
public interface IProviderBinding
{
    /// <summary>Gets the configured connection name, independent of any vendor (§13.1).</summary>
    string ProviderName { get; }

    /// <summary>Gets the endpoint, or null for an in-process adapter (§13.5).</summary>
    Uri? Endpoint { get; }

    /// <summary>
    /// Gets locality resolved from in-process execution or exact trusted-service matching;
    /// a private IP address alone never implies locality (§13.5).
    /// </summary>
    bool IsLocal { get; }

    /// <summary>
    /// Resolves model capabilities and limits from the catalog, then explicit overrides;
    /// unresolved required limits must be rejected by the consuming role (§13.3).
    /// </summary>
    ValueTask<ResolvedModel> ResolveAsync(
        string model,
        ModelLimits? limitsOverrides = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lists model identifiers through the policy-owned transport (§13.6, SEC-15).</summary>
    ValueTask<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks reachability through the policy-owned transport for readiness (§16, SEC-15).</summary>
    ValueTask<ProviderHealthResult> HealthCheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a binding-owned chat client for the model, or null when unsupported;
    /// callers must not dispose it or bypass its policy transport (§13.2, SEC-15).
    /// </summary>
    IChatClient? GetChatClient(string model);

    /// <summary>
    /// Gets a binding-owned embedding generator for the model, or null when unsupported;
    /// callers must not dispose it or bypass its policy transport (§13.2, SEC-15).
    /// </summary>
    IEmbeddingGenerator<string, Embedding<float>>? GetEmbeddingGenerator(string model);
}

/// <summary>A concrete provider and model's resolved declaration; see spec §13.3.</summary>
/// <param name="Provider">The configured provider connection name.</param>
/// <param name="Model">The provider's model identifier.</param>
/// <param name="Capabilities">Capabilities declared for this concrete model.</param>
/// <param name="Limits">Catalog limits with role configuration overrides applied.</param>
public sealed record ResolvedModel(
    string Provider,
    string Model,
    ModelCapabilities Capabilities,
    ModelLimits Limits);

/// <summary>A timestamped reachability result for readiness freshness; see spec §16.</summary>
/// <param name="IsHealthy">Whether the provider was reachable and healthy.</param>
/// <param name="CheckedAt">The UTC instant at which the check completed.</param>
/// <param name="Detail">An optional redacted diagnostic, never a secret or request body.</param>
public sealed record ProviderHealthResult(bool IsHealthy, DateTimeOffset CheckedAt, string? Detail = null);
