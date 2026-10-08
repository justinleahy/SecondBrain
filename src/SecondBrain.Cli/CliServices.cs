using Microsoft.AspNetCore.DataProtection;
using SecondBrain.Cli.Commands;
using SecondBrain.Cli.Credentials;
using SecondBrain.Cli.Provisioning;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Security;
using SecondBrain.Infrastructure.Security;
using SecondBrain.Infrastructure.Configuration;

namespace SecondBrain.Cli;

/// <summary>Explicit composition boundary for lane A/B/D command adapters at convergence.</summary>
public sealed class CliServices
{
    private readonly HttpDaemonCommands? http;
    private readonly LocalInitializationCommands? local;
    public CliServices(bool bindDefaults = true)
    {
        if (bindDefaults)
        {
            http = new HttpDaemonCommands(() => CredentialStore, (purpose, token) => ReadPassword(purpose, token));
            local = new LocalInitializationCommands(root => CreateKeyRing(root), (purpose, token) => ReadPassword(purpose, token), () => CalibratePassword());
            Initialization = local;
            EpochRevoker = local;
            Credentials = http;
            Providers = http;
            Sessions = http;
            Login = http;
            DoctorExtension = http;
        }
        else
        {
            var unbound = new UnboundCommands();
            Initialization = unbound;
            Credentials = unbound;
            Providers = unbound;
            Sessions = unbound;
            Login = unbound;
        }
    }
    public IInitializationCommands Initialization { get; init; }
    public ICredentialCommands Credentials { get; init; }
    public IProviderCommands Providers { get; init; }
    public ISessionCommands Sessions { get; init; }
    public ILoginCommands Login { get; init; }
    public IDoctorExtension? DoctorExtension { get; init; }
    public IAccountEpochRevoker? EpochRevoker { get; init; }
    public ICredentialStore CredentialStore { get; init; } = new OsCredentialStore(
        Environment.GetEnvironmentVariable("SECONDBRAIN_CREDENTIAL_DIRECTORY"),
        !string.Equals(Environment.GetEnvironmentVariable("SECONDBRAIN_CREDENTIAL_STORE"), "file", StringComparison.OrdinalIgnoreCase));
    public Func<string, CancellationToken, Task<string>> ReadPassword { get; init; } = PasswordInput.ReadAsync;
    public Func<PasswordParameters> CalibratePassword { get; init; } = () => PasswordHasher.Calibrate().Parameters;
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

    internal void ConfigureDaemon(Func<SecondBrainOptions> options, string? origin, string credentialName) =>
        http?.Configure(options, origin, credentialName);
    internal void ConfigureLocal(SecondBrainOptions options) => local?.Configure(options);
}
