using System.Text.RegularExpressions;

namespace SecondBrain.Core.Configuration;

/// <summary>Resolves the references retained by the frozen provider configuration contract.</summary>
public interface ISecretResolver
{
    string Resolve(string reference);
}

/// <summary>Environment takes precedence over a single named file in the read-only secrets directory.</summary>
public sealed partial class SecretResolver(string? secretsDirectory = null, Func<string, string?>? environment = null) : ISecretResolver
{
    private readonly string directory = Path.GetFullPath(secretsDirectory ?? "/etc/secondbrain/secrets");
    private readonly Func<string, string?> getEnvironment = environment ?? Environment.GetEnvironmentVariable;

    public string Resolve(string reference)
    {
        var match = ReferencePattern().Match(reference);
        if (!match.Success)
            throw new ConfigurationException("A secret must be a single ${NAME} reference.");
        var name = match.Groups[1].Value;
        var value = getEnvironment(name);
        if (value is null)
        {
            var path = Path.Combine(directory, name);
            // A symlink is not a secret mount: it can redirect a reference outside this directory.
            if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null)
                throw new ConfigurationException($"Reference ${{{name}}} has no environment value or secret file.");
            try { value = File.ReadAllText(path).TrimEnd('\r', '\n'); }
            catch (IOException) { throw new ConfigurationException($"Reference ${{{name}}} cannot be read."); }
            catch (UnauthorizedAccessException) { throw new ConfigurationException($"Reference ${{{name}}} cannot be read."); }
        }
        if (string.IsNullOrEmpty(value))
            throw new ConfigurationException($"Reference ${{{name}}} is empty.");
        return value;
    }

    internal string Expand(string value) => EmbeddedReferencePattern().Replace(value, match => Resolve(match.Value));

    [GeneratedRegex(@"^\$\{([A-Za-z_][A-Za-z0-9_]*)\}$", RegexOptions.CultureInvariant)]
    internal static partial Regex ReferencePattern();

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedReferencePattern();
}
