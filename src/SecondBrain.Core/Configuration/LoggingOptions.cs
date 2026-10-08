using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

/// <summary>Redacted logging settings from spec.md §15.9 SEC-24, §16 and Appendix B.</summary>
public sealed class LoggingOptions
{
    /// <summary>The configured minimum logging level.</summary>
    [YamlMember(Alias = "level")]
    public string Level { get; set; } = "info";

    /// <summary>Whether bounded retrieval traces are recorded.</summary>
    [YamlMember(Alias = "retrieval_trace")]
    public bool RetrievalTrace { get; set; }

    /// <summary>Whether request/document bodies may be logged, subject to debug-log retention.</summary>
    [YamlMember(Alias = "debug_bodies")]
    public bool DebugBodies { get; set; }
}
