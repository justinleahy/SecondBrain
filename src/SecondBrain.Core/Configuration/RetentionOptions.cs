using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

public sealed class RetentionOptions
{
    [YamlMember(Alias = "trash_days")] public int TrashDays { get; set; } = 30;
    [YamlMember(Alias = "removed_days")] public int RemovedDays { get; set; } = 90;
    [YamlMember(Alias = "resolved_proposal_days")] public int ResolvedProposalDays { get; set; } = 30;
    [YamlMember(Alias = "payload_days")] public int PayloadDays { get; set; } = 30;
    [YamlMember(Alias = "idempotency_hours")] public int IdempotencyHours { get; set; } = 24;
    [YamlMember(Alias = "trace_days")] public int TraceDays { get; set; } = 7;
    [YamlMember(Alias = "debug_log_days")] public int DebugLogDays { get; set; } = 1;

    public void Validate()
    {
        if (new[] { TrashDays, RemovedDays, ResolvedProposalDays, PayloadDays, TraceDays, DebugLogDays }.Any(days => days is < 1 or > 36500)
            || IdempotencyHours is < 1 or > 876000)
            throw new ConfigurationException("Retention periods must be positive and no longer than 100 years.");
    }
}
