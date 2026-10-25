using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

public sealed class EnrichmentOptions
{
    [YamlMember(Alias = "enabled")] public bool Enabled { get; set; } = true;
    [YamlMember(Alias = "write_back_frontmatter")] public bool WriteBackFrontmatter { get; set; }
    [YamlMember(Alias = "batch_backfill")] public bool BatchBackfill { get; set; } = true;
    public void Validate()
    {
    }
}
