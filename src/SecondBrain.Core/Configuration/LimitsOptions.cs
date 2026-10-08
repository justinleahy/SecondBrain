using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

/// <summary>Admission-limit groups from spec.md §15.8 and Appendix B.</summary>
public sealed class LimitsOptions
{
    /// <summary>The flat per-credential defaults declared in Appendix B.</summary>
    [YamlMember(Alias = "per_credential")]
    public CredentialLimitsOptions PerCredential { get; set; } = new();

    /// <summary>The instance-wide capacity limits declared in Appendix B.</summary>
    [YamlMember(Alias = "global")]
    public GlobalLimitsOptions Global { get; set; } = new();
}

/// <summary>Per-credential defaults from spec.md §15.8 and Appendix B.</summary>
public sealed class CredentialLimitsOptions
{
    /// <summary>Accepted requests per minute.</summary>
    [YamlMember(Alias = "requests_per_min")]
    public int RequestsPerMinute { get; set; } = 60;

    /// <summary>Concurrent upload reservations.</summary>
    [YamlMember(Alias = "concurrent_uploads")]
    public int ConcurrentUploads { get; set; } = 4;

    /// <summary>Concurrent search reservations.</summary>
    [YamlMember(Alias = "concurrent_searches")]
    public int ConcurrentSearches { get; set; } = 4;

    /// <summary>Concurrent assistant turn reservations.</summary>
    [YamlMember(Alias = "concurrent_turns")]
    public int ConcurrentTurns { get; set; } = 2;

    /// <summary>Open server-sent event streams.</summary>
    [YamlMember(Alias = "sse_streams")]
    public int SseStreams { get; set; } = 4;

    /// <summary>Open Blazor circuits.</summary>
    [YamlMember(Alias = "circuits")]
    public int Circuits { get; set; } = 4;

    /// <summary>Reserved and settled model tokens per hour.</summary>
    [YamlMember(Alias = "model_tokens_per_hour")]
    public long ModelTokensPerHour { get; set; } = 200_000;
}

/// <summary>Instance-wide admission capacities from spec.md §15.8 and Appendix B.</summary>
public sealed class GlobalLimitsOptions
{
    /// <summary>The maximum admitted files in one source.</summary>
    [YamlMember(Alias = "files_per_source")]
    public long FilesPerSource { get; set; } = 500_000;

    /// <summary>The maximum admitted files across the instance.</summary>
    [YamlMember(Alias = "files")]
    public long Files { get; set; } = 2_000_000;

    /// <summary>The maximum admitted bytes, expressed in gigabytes.</summary>
    [YamlMember(Alias = "admitted_gb")]
    public decimal AdmittedGb { get; set; } = 50;

    /// <summary>The maximum queued jobs.</summary>
    [YamlMember(Alias = "queued_jobs")]
    public int QueuedJobs { get; set; } = 10_000;

    /// <summary>The maximum occurrences in one calendar materialization.</summary>
    [YamlMember(Alias = "calendar_occurrences")]
    public int CalendarOccurrences { get; set; } = 100_000;

    /// <summary>The free-disk ingestion low-water threshold, expressed in gigabytes.</summary>
    [YamlMember(Alias = "disk_low_water_gb")]
    public decimal DiskLowWaterGb { get; set; } = 10;
}
