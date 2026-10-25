using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

public sealed class CalendarOptions
{
    [YamlMember(Alias = "past_months")] public int PastMonths { get; set; } = 12;
    [YamlMember(Alias = "future_months")] public int FutureMonths { get; set; } = 12;
}
