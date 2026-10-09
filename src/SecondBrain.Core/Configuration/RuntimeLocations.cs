namespace SecondBrain.Core.Configuration;

/// <summary>
/// The configuration file and secrets directory this process actually loads. FLD-1 protects them from source
/// registration wherever an operator relocates them, in addition to the fixed default locations.
/// </summary>
public sealed record RuntimeLocations
{
    public const string DefaultConfigPath = "/etc/secondbrain/config.yaml";
    public const string DefaultSecretsDirectory = "/etc/secondbrain/secrets";

    /// <summary>Null selects the default; relative paths resolve against the working directory, as the loader does.</summary>
    public RuntimeLocations(string? configPath, string? secretsDirectory)
    {
        ConfigPath = Path.GetFullPath(configPath ?? DefaultConfigPath);
        SecretsDirectory = Path.GetFullPath(secretsDirectory ?? DefaultSecretsDirectory);
    }

    /// <summary>The absolute configuration file path.</summary>
    public string ConfigPath { get; }

    /// <summary>The absolute external secrets directory.</summary>
    public string SecretsDirectory { get; }

    /// <summary>The directory that holds the configuration file.</summary>
    public string ConfigDirectory => Path.GetDirectoryName(ConfigPath) ?? ConfigPath;

    /// <summary>Reads the daemon's environment variables. The CLI forwards its resolved choices under these names.</summary>
    public static RuntimeLocations FromEnvironment(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return new RuntimeLocations(environment("SECONDBRAIN_CONFIG"), environment("SECONDBRAIN_SECRETS_DIRECTORY"));
    }
}
