using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

/// <summary>The instance-wide daily model token cap from spec.md §15.8 and Appendix B.</summary>
public sealed class BudgetOptions
{
    /// <summary>The daily cap against which model usage is reserved and settled.</summary>
    [YamlMember(Alias = "daily_tokens")]
    public long DailyTokens { get; set; } = 2_000_000;
}
