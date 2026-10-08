using System.Collections;

namespace SecondBrain.Extractor;

/// <summary>Keep only the service-manager and runtime values needed by the isolated extractor.</summary>
public static class ExtractorEnvironment
{
    private static readonly HashSet<string> AllowedNames = new(StringComparer.Ordinal)
    {
        "LISTEN_PID", "LISTEN_FDS", "NOTIFY_SOCKET", "TMPDIR", "PATH", "DOTNET_EnableDiagnostics"
    };

    /// <summary>Pure allowlist transformation, so callers/tests do not mutate process-global environment.</summary>
    public static IReadOnlyDictionary<string, string> Scrub(IReadOnlyDictionary<string, string?> inherited)
    {
        var scrubbed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in inherited)
        {
            if (AllowedNames.Contains(name) && value is not null)
            {
                scrubbed[name] = value;
            }
        }
        scrubbed["DOTNET_EnableDiagnostics"] = "0";
        return scrubbed;
    }

    public static void ScrubCurrentProcess()
    {
        var inherited = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString(), StringComparer.Ordinal);
        var scrubbed = Scrub(inherited);
        foreach (var name in inherited.Keys)
        {
            if (!scrubbed.ContainsKey(name))
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
        foreach (var (name, value) in scrubbed)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
