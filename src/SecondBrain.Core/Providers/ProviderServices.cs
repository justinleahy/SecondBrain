using System.Net;
using SecondBrain.Core.Configuration;

namespace SecondBrain.Core.Providers;

/// <summary>Redacted provider failures shared by adapters, readiness and HTTP problem mapping.</summary>
public sealed class ProviderRequestException : HttpRequestException
{
    public ProviderRequestException(string problemType, string message, bool retryable = false,
        HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException, statusCode)
    {
        ProblemType = problemType;
        Retryable = retryable;
    }

    public string ProblemType { get; }
    public bool Retryable { get; }
}

/// <summary>The configured role and fallback, resolved before any inference is admitted.</summary>
public sealed record ProviderRoleBinding(ModelRole Role, IProviderBinding Provider,
    ResolvedModel Model, ProviderRoleBinding? Fallback = null);

/// <summary>Provider-neutral CLI/HTTP integration; all diagnostic network operations use policy transport.</summary>
public interface IProviderRegistry
{
    /// <summary>Whether the registry has atomically accepted this exact validated options snapshot.</summary>
    bool IsCurrentConfiguration(SecondBrainOptions options);
    ProviderRoleBinding GetRole(ModelRole role);
    IProviderBinding GetProvider(string name);
    IReadOnlyList<string> ProviderNames { get; }
    ValueTask<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListModelsAsync(CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyDictionary<ModelRole, ProviderHealthResult>> TestAsync(CancellationToken cancellationToken = default);
}

/// <summary>Secret loading belongs to configuration; adapters never accept literal secrets in config.</summary>
public interface IProviderCredentialResolver
{
    string? Resolve(string? reference);
}
