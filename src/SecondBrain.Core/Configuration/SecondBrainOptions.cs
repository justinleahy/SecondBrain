using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

/// <summary>
/// M0 configuration values from spec.md §14, §19 and Appendix B. Loading, secret resolution,
/// validation and reload belong to the configuration lane.
/// </summary>
public sealed class SecondBrainOptions
{
    /// <summary>The private data root containing the vault, stores and writable key ring (§6).</summary>
    [YamlMember(Alias = "data_root")]
    public string DataRoot { get; set; } = "/srv/secondbrain";

    /// <summary>Listener and request-origin configuration (§15.2).</summary>
    [YamlMember(Alias = "server")]
    public ServerOptions Server { get; set; } = new();

    /// <summary>Interactive credential lifetimes and passkey availability (§15.3).</summary>
    [YamlMember(Alias = "auth")]
    public AuthOptions Auth { get; set; } = new();

    /// <summary>Named, provider-neutral connections; no provider is selected by default (§13).</summary>
    [YamlMember(Alias = "providers")]
    public Dictionary<string, ProviderOptions> Providers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Role bindings; provisioning supplies every required provider and model (§13.1).</summary>
    [YamlMember(Alias = "models")]
    public ModelRolesOptions Models { get; set; } = new();

    /// <summary>Provider-data and control-plane egress policy (§15.6).</summary>
    [YamlMember(Alias = "privacy")]
    public PrivacyOptions Privacy { get; set; } = new();

    /// <summary>Permitted source roots, separate from the private data root (§7.2).</summary>
    [YamlMember(Alias = "sources")]
    public SourcesOptions Sources { get; set; } = new();

    /// <summary>Admission and per-credential resource limits (§15.8).</summary>
    [YamlMember(Alias = "limits")]
    public LimitsOptions Limits { get; set; } = new();

    /// <summary>The instance-wide model token budget (§15.8 and Appendix B).</summary>
    [YamlMember(Alias = "budgets")]
    public BudgetOptions Budgets { get; set; } = new();

    /// <summary>Redacted logging configuration (§15.9 and §16).</summary>
    [YamlMember(Alias = "logging")]
    public LoggingOptions Logging { get; set; } = new();

    /// <summary>The instance time zone and week boundary (§5.5).</summary>
    [YamlMember(Alias = "time")]
    public TimeOptions Time { get; set; } = new();

    /// <summary>The isolated extractor's Unix socket, used for readiness and diagnostics.</summary>
    [YamlMember(Alias = "extractor")]
    public ExtractorOptions Extractor { get; set; } = new();

    [YamlMember(Alias = "ingest")]
    public IngestOptions Ingest { get; set; } = new();

    [YamlMember(Alias = "retrieval")]
    public RetrievalOptions Retrieval { get; set; } = new();

    [YamlMember(Alias = "assistant")]
    public AssistantOptions Assistant { get; set; } = new();
}

public sealed class AssistantOptions
{
    [YamlMember(Alias = "auto_approve_writes")]
    public bool AutoApproveWrites { get; set; }
    [YamlMember(Alias = "max_tool_calls_per_turn")]
    public int MaxToolCallsPerTurn { get; set; } = 8;
    [YamlMember(Alias = "max_read_documents")]
    public int MaxReadDocuments { get; set; } = 12;
    [YamlMember(Alias = "max_read_bytes")]
    public int MaxReadBytes { get; set; } = 200_000;
    [YamlMember(Alias = "allow_external_writes")]
    public bool AllowExternalWrites { get; set; }
    [YamlMember(Alias = "pending_ttl_hours")]
    public int PendingTtlHours { get; set; } = 72;
}

public sealed class RetrievalOptions
{
    [YamlMember(Alias = "max_exact_candidates")]
    public int MaxExactCandidates { get; set; } = 1_000_000;
}

public sealed class IngestOptions
{
    [YamlMember(Alias = "sync_embed_deadline_ms")]
    public int SyncEmbedDeadlineMs { get; set; } = 3000;

    /// <summary>The local wall-clock time (<c>HH:mm</c>, in <c>time.zone</c>) of the nightly deep rescan (FLD-2).</summary>
    [YamlMember(Alias = "deep_rescan_at")]
    public string DeepRescanAt { get; set; } = "03:00";
}

public sealed class ExtractorOptions
{
    [YamlMember(Alias = "socket_path")]
    public string SocketPath { get; set; } = "/run/secondbrain/extractor.sock";
}
