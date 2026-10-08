using Microsoft.AspNetCore.DataProtection;
using SecondBrain.Cli.Commands;
using SecondBrain.Cli.Credentials;
using SecondBrain.Cli.Provisioning;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Security;

namespace SecondBrain.Cli;

/// <summary>Explicit composition boundary for lane A/B/D command adapters at convergence.</summary>
public sealed class CliServices
{
    private readonly UnboundCommands _unbound = new();
    public CliServices()
    {
        Initialization = _unbound;
        Credentials = _unbound;
        Providers = _unbound;
        Sessions = _unbound;
        Login = _unbound;
    }
    public IInitializationCommands Initialization { get; init; }
    public ICredentialCommands Credentials { get; init; }
    public IProviderCommands Providers { get; init; }
    public ISessionCommands Sessions { get; init; }
    public ILoginCommands Login { get; init; }
    public IDoctorExtension? DoctorExtension { get; init; }
    public IAccountEpochRevoker? EpochRevoker { get; init; }
    public ICredentialStore CredentialStore { get; init; } = new OsCredentialStore();
    public IDaemonCommands Daemon { get; init; } = new InstalledDaemonCommands();
    public ProvisioningService Provisioning { get; init; } = new();
    public Func<string, string?, SecondBrainOptions> ReadConfiguration { get; init; } = (path, secrets) => new YamlConfigurationLoader(secrets).Load(path);
    public Func<string, FileKeyRing> CreateKeyRing { get; init; } = dataRoot =>
    {
        UnixSecurity.SetPrivateUmask();
        var directory = Path.Combine(dataRoot, "keyring");
        var provider = DataProtectionProvider.Create(new DirectoryInfo(directory), builder => builder.SetApplicationName(KeyRingPurposes.ApplicationName));
        return new FileKeyRing(dataRoot, provider);
    };
}
