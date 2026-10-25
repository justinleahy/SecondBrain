using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

public sealed class BackupOptions
{
    [YamlMember(Alias = "keep")] public int Keep { get; set; } = 14;
    [YamlMember(Alias = "key_file")] public string KeyFile { get; set; } = "/etc/secondbrain/secrets/backup.key";
    [YamlMember(Alias = "segment_mib")] public int SegmentMib { get; set; } = 1;
    [YamlMember(Alias = "include_trash")] public bool IncludeTrash { get; set; } = true;
    [YamlMember(Alias = "external_roots")] public List<string> ExternalRoots { get; set; } = [];
}
