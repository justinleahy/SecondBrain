namespace SecondBrain.Cli.Provisioning;

/// <summary>Every installation path and Unix identity can be overridden for an isolated installation.</summary>
public sealed record ProvisionOptions
{
    public string DataRoot { get; init; } = "/srv/secondbrain";
    public string IncomingRoot { get; init; } = "/srv/secondbrain-incoming";
    public string ConfigDirectory { get; init; } = "/etc/secondbrain";
    public string RuntimeDirectory { get; init; } = "/run/secondbrain";
    public string UnitsDirectory { get; init; } = "/etc/systemd/system";
    public string TmpFilesDirectory { get; init; } = "/etc/tmpfiles.d";
    public string ComposeDirectory { get; init; } = "/opt/secondbrain";
    public string TemplatesDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "deploy");
    public string Deployment { get; init; } = "systemd";
    public string DaemonUser { get; init; } = "secondbrain";
    public string ExtractorUser { get; init; } = "secondbrain-extract";
    public string SyncUser { get; init; } = "secondbrain-sync";
    public uint DaemonUid { get; init; } = 1654;
    public uint DaemonGid { get; init; } = 1654;
    public uint ExtractorUid { get; init; } = 1655;
    public uint SyncUid { get; init; } = 1656;
    public bool DryRun { get; init; }
    public bool SkipUsers { get; init; }
}

public sealed record ProvisionAction(string Kind, string Target, string Detail);
public sealed record ProvisionResult(bool DryRun, IReadOnlyList<ProvisionAction> Actions);

/// <summary>Injectable privileged operating-system actions; fake implementations support temp-root tests.</summary>
public interface IProvisionPlatform
{
    bool IsRoot { get; }
    void EnsureGroup(string name, uint gid);
    void EnsureUser(string name, uint uid, string group);
    void SetOwner(string path, uint uid, uint gid);
}
