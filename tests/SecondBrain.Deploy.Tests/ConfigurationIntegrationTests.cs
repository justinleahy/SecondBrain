using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Server.Http;
using SecondBrain.Core.Configuration;
using SecondBrain.Infrastructure.Configuration;
using SecondBrain.Core.Security;
using SecondBrain.Server.Composition;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class ConfigurationIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(UnixPath.Canonicalize(Path.GetTempPath()), "secondbrain-dp-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(root, "data");
    private string Incoming => Path.Combine(root, "incoming");

    public ConfigurationIntegrationTests()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Data);
        else Directory.CreateDirectory(Data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Incoming);
    }

    private FileKeyRing OpenRing()
    {
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Data, "keyring")),
            builder => builder.SetApplicationName(KeyRingPurposes.ApplicationName));
        return new FileKeyRing(Data, provider);
    }

    [Fact]
    public async Task ProductionKeyRingAndHttpProtectionComposeWithoutRecursion()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<SecondBrainOptions>(options => options.DataRoot = Data);
        services.AddKeyRing();
        // This fixture owns a real lock but does not require privileged incoming-root chown.
        services.AddSingleton(_ => DataRootLock.Acquire(Data));
        services.AddHttpHost();
        await using var container = services.BuildServiceProvider();
        var ring = container.GetRequiredService<IKeyRing>();
        var framework = container.GetRequiredService<IDataProtectionProvider>().CreateProtector("framework");
        var ciphertext = framework.Protect("probe");
        Assert.Equal("probe", ring.DataProtectionProvider.CreateProtector(KeyRingPurposes.Antiforgery).CreateProtector("framework").Unprotect(ciphertext));
        Assert.True(File.Exists(Path.Combine(Data, "keyring", "hmac.json")));
    }

    [Fact]
    public async Task KeyRingDataProtectionPersistsAndPurposesAreSeparated()
    {
        string protectedValue;
        byte[] oldSignature;
        using (var ring = OpenRing())
        {
            protectedValue = ring.DataProtectionProvider.CreateProtector(KeyRingPurposes.Session).Protect("session-state");
            oldSignature = ring.Sign(ring.ActiveKid, [1, 2, 3]);
            await ring.RotateAsync();
            foreach (var purpose in new[] { KeyRingPurposes.Cursor, KeyRingPurposes.Antiforgery, KeyRingPurposes.Capability })
                Assert.Throws<System.Security.Cryptography.CryptographicException>(() => ring.DataProtectionProvider.CreateProtector(purpose).Unprotect(protectedValue));
        }
        using var reopened = OpenRing();
        Assert.Equal("session-state", reopened.DataProtectionProvider.CreateProtector(KeyRingPurposes.Session).Unprotect(protectedValue));
        Assert.True(reopened.Verify([1, 2, 3], oldSignature));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(Data, "keyring"), "key-*.xml"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Data, "keyring")))
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact]
    public void ConfigurationOptionsMonitorPublishesOnlyValidChangesAndDisposesSubscriptions()
    {
        var path = Path.Combine(root, "config.yaml");
        var yaml = $"data_root: {Data}\nserver: {{ listeners: [{{ bind: 127.0.0.1, port: 7171 }}], hosts: [localhost], origins: [http://localhost:7171] }}\nsources: {{ incoming_root: {Incoming}, allowed_roots: [{Incoming}] }}\nlogging: {{ level: info }}";
        File.WriteAllText(path, yaml);
        using var source = new ReloadingConfiguration(path, new YamlConfigurationLoader(environment: _ => null), watch: false);
        IOptionsMonitor<SecondBrainOptions> monitor = new SecondBrainOptionsMonitor(source);
        var changed = new List<SecondBrainOptions>();
        using (monitor.OnChange((value, name) => { Assert.Equal(Options.DefaultName, name); changed.Add(value); }))
        {
            File.WriteAllText(path, yaml.Replace("level: info", "level: debug", StringComparison.Ordinal));
            Assert.True(source.TryReload());
            Assert.Same(Assert.Single(changed), monitor.CurrentValue);
            File.WriteAllText(path, "privacy: null");
            Assert.False(source.TryReload());
            Assert.Equal("debug", monitor.CurrentValue.Logging.Level);
            Assert.Single(changed);
        }
        File.WriteAllText(path, yaml);
        Assert.True(source.TryReload());
        Assert.Single(changed);
        Assert.Same(monitor.CurrentValue, monitor.Get(""));
    }

    public void Dispose() => Directory.Delete(root, true);
}
