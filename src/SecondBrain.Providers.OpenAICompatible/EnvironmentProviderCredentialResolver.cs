using SecondBrain.Core.Providers;

namespace SecondBrain.Providers.OpenAICompatible;

/// <summary>Environment-backed resolver; the configuration lane may replace it with its secrets-directory resolver.</summary>
public sealed class EnvironmentProviderCredentialResolver : IProviderCredentialResolver
{
    public string? Resolve(string? reference)
    {
        if (reference is null) return null;
        if (!reference.StartsWith("${", StringComparison.Ordinal) || !reference.EndsWith('}') || reference.Length < 4)
            throw new InvalidOperationException("A provider credential must be a secret reference.");
        return Environment.GetEnvironmentVariable(reference[2..^1])
            ?? throw new InvalidOperationException("A provider credential reference could not be resolved.");
    }
}
