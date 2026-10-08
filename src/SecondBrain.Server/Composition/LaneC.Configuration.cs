using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Security;

namespace SecondBrain.Server.Composition;

/// <summary>Lane C composition: configuration and keys (spec §14, §15.9).</summary>
public static class LaneCConfiguration
{
    /// <summary>Registers configuration services; implemented in M0 item 2.</summary>
    public static IServiceCollection AddConfiguration(this IServiceCollection services)
    {
        services.AddSingleton(_ => new YamlConfigurationLoader(Environment.GetEnvironmentVariable("SECONDBRAIN_SECRETS_DIRECTORY")));
        services.AddSingleton<ISecretResolver>(provider => provider.GetRequiredService<YamlConfigurationLoader>().Secrets);
        services.AddSingleton(provider => new ReloadingConfiguration(
            Environment.GetEnvironmentVariable("SECONDBRAIN_CONFIG") ?? "/etc/secondbrain/config.yaml",
            provider.GetRequiredService<YamlConfigurationLoader>(),
            message => provider.GetRequiredService<ILogger<ReloadingConfiguration>>().LogWarning("{ConfigurationDiagnostic}", message)));
        services.AddSingleton<IOptionsMonitor<SecondBrainOptions>, SecondBrainOptionsMonitor>();
        services.AddSingleton<IOptions<SecondBrainOptions>>(provider => new OptionsWrapper<SecondBrainOptions>(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue));
        return services;
    }

    /// <summary>Registers key-ring services; implemented in M0 item 3a.</summary>
    public static IServiceCollection AddKeyRing(this IServiceCollection services)
    {
        UnixSecurity.SetPrivateUmask();
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue;
            var daemonUid = RootSecurityValidator.CurrentUid();
            if (daemonUid == 0) throw new UnauthorizedAccessException("The daemon must run as a non-root user.");
            var syncUid = ReadUid("SECONDBRAIN_SYNC_UID", "secondbrain-sync");
            RootSecurityValidator.ValidateDaemonIdentities(daemonUid, syncUid);
            RootSecurityValidator.ValidateOrThrow(options.DataRoot, options.Sources.IncomingRoot, daemonUid, RootSecurityValidator.CurrentGid(), syncUid);
            return DataRootLock.Acquire(options.DataRoot);
        });
        services.AddSingleton<IDataProtectionProvider>(provider =>
        {
            _ = provider.GetRequiredService<DataRootLock>();
            var root = provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.DataRoot;
            return DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keyring")), builder => builder.SetApplicationName(KeyRingPurposes.ApplicationName));
        });
        services.AddSingleton<FileKeyRing>(provider => new FileKeyRing(
            provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.DataRoot,
            provider.GetRequiredService<IDataProtectionProvider>()));
        services.AddSingleton<IKeyRing>(provider => provider.GetRequiredService<FileKeyRing>());
        services.AddHostedService<KeyRingStartup>();
        return services;
    }

    private static uint ReadUid(string environmentName, string account)
    {
        var value = Environment.GetEnvironmentVariable(environmentName);
        if (value is not null) return uint.TryParse(value, out var uid) ? uid : throw new ConfigurationException("Sync user id must be numeric.");
        return UnixAccounts.UserId(account) ?? throw new ConfigurationException("Sync user does not exist; provision it or configure SECONDBRAIN_SYNC_UID.");
    }
}

/// <summary>Adapter exposes the validated Core snapshots through the standard monitor contract.</summary>
public sealed class SecondBrainOptionsMonitor(ReloadingConfiguration configuration) : IOptionsMonitor<SecondBrainOptions>
{
    public SecondBrainOptions CurrentValue => configuration.CurrentValue;
    public SecondBrainOptions Get(string? name) => CurrentValue;
    public IDisposable OnChange(Action<SecondBrainOptions, string?> listener)
    {
        void Handler(SecondBrainOptions options) => listener(options, Options.DefaultName);
        configuration.Changed += Handler;
        return new Subscription(() => configuration.Changed -= Handler);
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? action = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
}

internal sealed class KeyRingStartup(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = services.GetRequiredService<DataRootLock>();
        _ = services.GetRequiredService<IKeyRing>();
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
