using SecondBrain.Core.Configuration;
using SecondBrain.Infrastructure.Configuration;
using SecondBrain.Infrastructure.Security;
using System.Runtime.InteropServices;
using Xunit;

namespace SecondBrain.Infrastructure.Tests.Configuration;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string root = Path.Combine(UnixPath.Canonicalize(Path.GetTempPath()), "secondbrain-config-" + Guid.NewGuid().ToString("N"));
    private string Incoming => Path.Combine(root, "incoming");
    private string Data => Path.Combine(root, "data");
    private string Secrets => Path.Combine(root, "secrets");
    private string Valid => $$"""
        data_root: {{Data}}
        providers:
          local: { adapter: in_process }
        models:
          chat: { provider: local, model: chat }
          enrich: { provider: local, model: enrich }
          embed: { provider: local, model: embed, dimensions: 1024 }
        server:
          listeners: [{ scheme: http, bind: 127.0.0.1, port: 7171 }]
          hosts: [localhost, 127.0.0.1]
          origins: [http://127.0.0.1:7171]
        sources:
          allowed_roots: [{{Incoming}}]
          incoming_root: {{Incoming}}
        """;

    public ConfigurationTests()
    {
        Directory.CreateDirectory(Incoming);
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Secrets);
    }

    private YamlConfigurationLoader Loader(Func<string, string?>? environment = null) => new(Secrets, environment ?? (_ => null));

    [Fact]
    public void BackupConfigurationLoadsValidatesAndOnlyReloadsRetentionCount()
    {
        var path=Path.Combine(root,"config.yaml");
        var initial=Valid+"\nbackup: {keep: 14, key_file: "+Path.Combine(Secrets,"backup.key")+", external_roots: ["+Incoming+"]}\n";
        File.WriteAllText(path,initial);using var reload=new ReloadingConfiguration(path,Loader(),watch:false);
        Assert.Equal(14,reload.CurrentValue.Backup.Keep);Assert.Single(reload.CurrentValue.Backup.ExternalRoots);
        File.WriteAllText(path,initial.Replace("keep: 14","keep: 7",StringComparison.Ordinal));Assert.True(reload.TryReload());Assert.Equal(7,reload.CurrentValue.Backup.Keep);
        File.WriteAllText(path,initial.Replace("backup.key","other.key",StringComparison.Ordinal));Assert.False(reload.TryReload());
        Assert.Throws<ConfigurationException>(()=>Loader().LoadText(Valid+"\nbackup: {key_file: "+Path.Combine(Data,"backup.key")+"}\n"));
        Assert.Throws<ConfigurationException>(()=>Loader().LoadText(Valid+"\nbackup: {keep: 0}\n"));
        Assert.Throws<ConfigurationException>(()=>Loader().LoadText(Valid+"\nbackup: {segment_mib: 2}\n"));
        Assert.Throws<ConfigurationException>(()=>Loader().LoadText(Valid+"\nbackup: {include_trash: false}\n"));
        Assert.Throws<ConfigurationException>(()=>Loader().LoadText(Valid+"\nbackup: {external_roots: ["+Secrets+"]}\n"));
    }

    [Fact]
    public void RetentionConfigurationIsLoadedAndValidated()
    {
        var policy = Loader().LoadText(Valid + "\nretention:\n  trash_days: 45\n  payload_days: 10\n  idempotency_hours: 12\n").Retention;
        Assert.Equal(45, policy.TrashDays); Assert.Equal(10, policy.PayloadDays); Assert.Equal(12, policy.IdempotencyHours);
        Assert.Throws<ConfigurationException>(() => Loader().LoadText(Valid + "\nretention: {payload_days: 0}\n"));
        Assert.Throws<ConfigurationException>(() => Loader().LoadText(Valid + "\nretention: null\n"));
    }

    [Fact]
    public void CalendarConfigurationRetainsOnlyTheReferenceAndResolvesFromASecretFile()
    {
        File.WriteAllText(Path.Combine(Secrets, "CALENDAR_LINK"), "webcal://feed.test/published/2/private-fixture\n");
        var settings = Loader().LoadText(CalendarYaml("'${CALENDAR_LINK}'"));
        var calendar = Assert.Single(settings.Sources.CalendarSubscriptions);
        Assert.Equal("${CALENDAR_LINK}", calendar.UrlReference);
        Assert.DoesNotContain("private-fixture", System.Text.Json.JsonSerializer.Serialize(settings));
        Assert.Equal(3600, calendar.PollIntervalSeconds); Assert.True(calendar.Enabled);
        Assert.Empty(settings.Privacy.TrustedServices);
    }

    [Theory]
    [InlineData("'https://feed.test/private-fixture'")]
    [InlineData("'${CALENDAR_LINK}/extra'")]
    [InlineData("''")]
    [InlineData("null")]
    public void CalendarConfigurationRejectsInlineOrPartialReferences(string value)
    {
        var error = Assert.Throws<ConfigurationException>(() => Loader(name => name == "CALENDAR_LINK" ? "https://feed.test/private-fixture" : null).LoadText(CalendarYaml(value)));
        Assert.DoesNotContain("private-fixture", error.Message);
    }

    [Theory]
    [InlineData("ftp://feed.test/private-fixture")]
    [InlineData("https://other.test/private-fixture")]
    [InlineData("https://person:private-fixture@feed.test")]
    public void CalendarConfigurationValidatesResolvedLinksWithoutDisclosingThem(string resolved)
    {
        var error = Assert.Throws<ConfigurationException>(() => Loader(name => name == "CALENDAR_LINK" ? resolved : null).LoadText(CalendarYaml("'${CALENDAR_LINK}'")));
        Assert.DoesNotContain("private-fixture", error.Message);
    }

    [Fact]
    public void CalendarReferenceCannotBeAliasedIntoAnOrdinaryField()
    {
        var yaml = CalendarYaml("&private '${CALENDAR_LINK}'").Replace("name: Shared", "name: *private", StringComparison.Ordinal);
        Assert.Throws<ConfigurationException>(() => Loader(name => name == "CALENDAR_LINK" ? "https://feed.test/private-fixture" : null).LoadText(yaml));
    }

    [Fact]
    public void CalendarConfigurationRequiresUniqueIdsAndBounds()
    {
        foreach (var replacement in new[] { "id: ''", "id: 'path/not-id'", "name: ''", "poll_interval_s: 299" })
        {
            var yaml = CalendarYaml("'${CALENDAR_LINK}'");
            yaml = replacement.StartsWith("name:", StringComparison.Ordinal) ? yaml.Replace("name: Shared", replacement, StringComparison.Ordinal) :
                replacement.StartsWith("id:", StringComparison.Ordinal) ? yaml.Replace("id: shared", replacement, StringComparison.Ordinal) : yaml.Replace("      url: '${CALENDAR_LINK}'", "      url: '${CALENDAR_LINK}'\n      " + replacement, StringComparison.Ordinal);
            Assert.Throws<ConfigurationException>(() => Loader(name => name == "CALENDAR_LINK" ? "https://feed.test/private-fixture" : null).LoadText(yaml));
        }
        var duplicate = CalendarYaml("'${CALENDAR_LINK}'").Replace("  allowed_roots:", "    - { id: shared, name: Other, url: '${CALENDAR_LINK}' }\n  allowed_roots:", StringComparison.Ordinal);
        Assert.Throws<ConfigurationException>(() => Loader(name => name == "CALENDAR_LINK" ? "https://feed.test/private-fixture" : null).LoadText(duplicate));
    }

    private string CalendarYaml(string value) => Valid.Replace("sources:", "sources:\n  calendar_allowed_origins: ['https://feed.test']\n  calendar_subscriptions:\n    - id: shared\n      name: Shared\n      url: " + value, StringComparison.Ordinal);

    [Theory]
    [InlineData("http://feed.test")]
    [InlineData("webcal://feed.test")]
    [InlineData("https://feed.test/path")]
    [InlineData("https://feed.test?secret=private-value")]
    [InlineData("https://user:private-value@feed.test")]
    public void CalendarOriginPermissionsRejectPathsCredentialsAndOtherSchemes(string origin)
    {
        var yaml = Valid.Replace("sources:", "sources:\n  calendar_allowed_origins: ['" + origin + "']", StringComparison.Ordinal);
        var error = Assert.Throws<ConfigurationException>(() => Loader().LoadText(yaml));
        Assert.DoesNotContain("private-value", error.Message);
    }

    [Fact]
    public void CalendarOriginPermissionIsSeparateFromProviderTrust()
    {
        var yaml = Valid.Replace("sources:", "sources:\n  calendar_allowed_origins: ['https://feed.test']", StringComparison.Ordinal);
        var options = Loader().LoadText(yaml);
        Assert.Equal(new[] { "https://feed.test" }, options.Sources.CalendarAllowedOrigins);
        Assert.DoesNotContain("https://feed.test", options.Privacy.TrustedServices);
    }

    [Theory]
    [InlineData("listeners: [{ scheme: http, bind: 127.0.0.1, port: 7171 }]", "listeners: []")]
    [InlineData("hosts: [localhost, 127.0.0.1]", "hosts: []")]
    [InlineData("origins: [http://127.0.0.1:7171]", "origins: []")]
    public void ConfigRequiresExplicitServingSettings(string before, string after) =>
        Assert.Throws<ConfigurationException>(() => Loader().LoadText(Valid.Replace(before, after, StringComparison.Ordinal)));

    [Theory]
    [InlineData("http://user:secret@localhost:7171")]
    [InlineData("http://localhost:7171/path")]
    [InlineData("http://localhost:7171/")]
    [InlineData("http://localhost:7171?query=value")]
    [InlineData("ftp://localhost:7171")]
    public void ConfigRejectsNonOriginAllowlistEntries(string origin) =>
        Assert.Throws<ConfigurationException>(() => Loader().LoadText(Valid.Replace("http://127.0.0.1:7171", origin, StringComparison.Ordinal)));

    [Fact]
    public void NullExtractorReloadWithEnvironmentOverrideKeepsLastGoodSnapshot()
    {
        var path = Path.Combine(root, "config.yaml");
        File.WriteAllText(path, Valid);
        using var configuration = new ReloadingConfiguration(path,
            Loader(name => name == "SECONDBRAIN_EXTRACTOR_SOCKET" ? "/tmp/extractor.sock" : null), watch: false);
        var previous = configuration.CurrentValue;
        File.WriteAllText(path, Valid + "\nextractor: null\n");
        Assert.False(configuration.TryReload());
        Assert.Same(previous, configuration.CurrentValue);
    }

    [Fact]
    public void ConfigLoadsSnakeCaseM0SubsetAndDeferredAppendixSections()
    {
        var options = Loader().LoadText(Valid + "\nauth: { session_idle_hours: 6 }\nlimits: { per_credential: { requests_per_min: 12 } }\nlogging: { debug_bodies: false }\ningest: { concurrency: 4 }\nsandbox: { socket: /run/secondbrain/extractor.sock }\n");
        Assert.Equal(Data, options.DataRoot);
        Assert.Equal("in_process", options.Providers["local"].Kind);
        Assert.Equal(1024, options.Models.Embed!.Dimensions);
        Assert.Equal(6, options.Auth.SessionIdleHours);
        Assert.Equal(12, options.Limits.PerCredential.RequestsPerMinute);
        Assert.False(options.Logging.DebugBodies);
    }

    [Theory]
    [InlineData(null, "03:00")]
    [InlineData("\"01:30\"", "01:30")]
    public void DeepRescanTimeDefaultsToNightlyAndIsConfigurable(string? configured, string expected)
    {
        var options = Loader().LoadText(configured is null ? Valid : Valid + $"\ningest: {{ deep_rescan_at: {configured} }}\n");
        Assert.Equal(expected, options.Ingest.DeepRescanAt);
    }

    [Theory]
    [InlineData("\"25:00\"")]
    [InlineData("\"3am\"")]
    [InlineData("\"\"")]
    public void DeepRescanTimeMustBeATwentyFourHourTime(string configured) =>
        Assert.Contains("deep_rescan_at", Assert.Throws<ConfigurationException>(() => Loader().LoadText(Valid + $"\ningest: {{ deep_rescan_at: {configured} }}\n")).Message);

    [Theory]
    [InlineData("provider: local", "provider: missing", "declared provider")]
    [InlineData("dimensions: 1024", "dimensions: -1", "positive")]
    [InlineData("dimensions: 1024", "dimensions: 1024, limits: { embed_dimensions: 768 }", "conflict")]
    [InlineData("scheme: http", "scheme: https", "certificate")]
    [InlineData("port: 7171", "port: 0", "port")]
    [InlineData("scheme: http", "scheme: ftp", "scheme")]
    [InlineData("bind: 127.0.0.1", "bind: 0.0.0.0", "interface")]
    [InlineData("bind: 127.0.0.1", "bind: 8.8.8.8", "private")]
    [InlineData("adapter: in_process", "adapter: in_process, kind: hosted", "aliases")]
    [InlineData("adapter: in_process", "adapter: in_process, endpoint: https://a.test, base_url: https://b.test", "aliases")]
    [InlineData("adapter: in_process", "adapter: hosted, endpoint: https://hosted.test", "local_only")]
    [InlineData("adapter: in_process", "adapter: in_process, endpoint: https://hosted.test", "local_only")]
    [InlineData("model: chat", "model: chat, fallback: { provider: missing, model: fallback }", "declared provider")]
    public void ConfigRejectsInvalidRules(string oldValue, string newValue, string message)
        => Assert.Contains(message, Assert.Throws<ConfigurationException>(() => Loader().LoadText(Valid.Replace(oldValue, newValue, StringComparison.Ordinal))).Message);

    [Theory]
    [InlineData("relative")]
    [InlineData("/nonexistent-secondbrain-root")]
    [InlineData("$DATA")]
    [InlineData("$DATA/child")]
    public void ConfigRejectsInvalidAllowedRoots(string candidate)
    {
        Directory.CreateDirectory(Path.Combine(Data, "child"));
        candidate = candidate.Replace("$DATA", Data, StringComparison.Ordinal);
        Assert.Throws<ConfigurationException>(() => Loader().LoadText(Valid.Replace($"allowed_roots: [{Incoming}]", $"allowed_roots: [{candidate}]", StringComparison.Ordinal)));
    }

    [Fact]
    public void ConfigRejectsRootSymlinkIntoDataRoot()
    {
        var link = Path.Combine(root, "linked");
        Directory.CreateSymbolicLink(link, Data);
        Assert.Throws<ConfigurationException>(() => Loader().LoadText(Valid.Replace($"allowed_roots: [{Incoming}]", $"allowed_roots: [{link}]", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("https://service.test:8443", "https://service.test:8443/v1", true)]
    [InlineData("https://service.test:8443", "https://service.test/v1", false)]
    [InlineData("http://service.test:8443", "https://service.test:8443/v1", false)]
    public void ConfigLocalityUsesExactOrigin(string trusted, string endpoint, bool local)
    {
        var yaml = Valid.Replace("adapter: in_process", $"adapter: openai_compatible, base_url: {endpoint}", StringComparison.Ordinal) + $"\nprivacy: {{ local_only: true, trusted_services: ['{trusted}'] }}";
        if (local) Assert.NotNull(Loader().LoadText(yaml));
        else Assert.Throws<ConfigurationException>(() => Loader().LoadText(yaml));
    }

    [Fact]
    public void ConfigRejectsHostedFallbackEvenWithLocalPrimary()
    {
        var yaml = Valid.Replace("local: { adapter: in_process }", "local: { adapter: in_process }\n  hosted: { adapter: remote }", StringComparison.Ordinal)
            .Replace("model: chat", "model: chat, fallback: { provider: hosted, model: fallback }", StringComparison.Ordinal);
        Assert.Contains("local_only", Assert.Throws<ConfigurationException>(() => Loader().LoadText(yaml)).Message);
    }

    [Fact]
    public void ConfigAcceptsIdenticalProviderAliases()
    {
        var options = Loader().LoadText(Valid.Replace("adapter: in_process", "adapter: openai_compatible, kind: openai_compatible, endpoint: http://localhost/v1, base_url: http://localhost/v1", StringComparison.Ordinal) + "\nprivacy: { local_only: false }");
        Assert.Equal("http://localhost/v1", options.Providers["local"].Endpoint);
    }

    [Fact]
    public void ConfigSecretEnvironmentWinsOverFileAndOptionsRetainReference()
    {
        File.WriteAllText(Path.Combine(Secrets, "MODEL_KEY"), "file-value\n");
        var loader = Loader(name => name == "MODEL_KEY" ? "environment-value" : null);
        var options = loader.LoadText(Valid.Replace("adapter: in_process", "adapter: in_process, api_key: '${MODEL_KEY}'", StringComparison.Ordinal));
        Assert.Equal("${MODEL_KEY}", options.Providers["local"].ApiKeyReference);
        Assert.Equal("environment-value", loader.Secrets.Resolve(options.Providers["local"].ApiKeyReference!));
    }

    [Fact]
    public void ConfigSecretFileResolutionTrimsOnlyTrailingNewlines()
    {
        File.WriteAllText(Path.Combine(Secrets, "MODEL_KEY"), " value \r\n");
        Assert.Equal(" value ", Loader().Secrets.Resolve("${MODEL_KEY}"));
    }

    [Fact]
    public void ConfigExpandsEnvironmentIntoNonSecretFields()
    {
        var options = Loader(name => name == "DATA" ? Data : null).LoadText(Valid.Replace(Data, "${DATA}", StringComparison.Ordinal));
        Assert.Equal(Data, options.DataRoot);
    }

    [Fact]
    public void ConfigEnvironmentDataRootOverridesFile()
    {
        var overridden = Path.Combine(root, "other-data");
        var options = Loader(name => name == "SECONDBRAIN_DATA_ROOT" ? overridden : null).LoadText(Valid);
        Assert.Equal(overridden, options.DataRoot);
    }

    [Fact]
    public void ConfigSandboxChangesRequireRestart()
    {
        var path = Path.Combine(root, "config.yaml");
        File.WriteAllText(path, Valid + "\nsandbox: { socket: /run/first.sock }");
        using var source = new ReloadingConfiguration(path, Loader(), watch: false);
        File.WriteAllText(path, Valid + "\nsandbox: { socket: /run/second.sock }");
        Assert.False(source.TryReload());
    }

    [Theory]
    [InlineData("api_key: 'do-not-log-this-value'")]
    [InlineData("api_key: ${MISSING}")]
    [InlineData("api_key: [wrong]")]
    [InlineData("endpoint: https://username:do-not-log-this-value@example.test")]
    public void ConfigRejectsSecretsAndNeverEchoesTheirValues(string field)
    {
        var error = Assert.Throws<ConfigurationException>(() => Loader().LoadText(Valid.Replace("adapter: in_process", $"adapter: in_process, {field}", StringComparison.Ordinal)));
        Assert.DoesNotContain("do-not-log-this-value", error.ToString());
    }

    [Fact]
    public void ConfigSecretSymlinkAndTraversalAreRejected()
    {
        var outside = Path.Combine(root, "outside");
        File.WriteAllText(outside, "secret");
        File.CreateSymbolicLink(Path.Combine(Secrets, "LINK"), outside);
        Assert.Throws<ConfigurationException>(() => Loader().Secrets.Resolve("${LINK}"));
        Assert.Throws<ConfigurationException>(() => Loader().Secrets.Resolve("${../outside}"));
    }

    [Fact]
    public void ConfigSecretAliasCannotMutateReferenceOrExposeResolvedValue()
    {
        var yaml = Valid.Replace("adapter: in_process", "adapter: in_process, api_key: &key '${KEY}'", StringComparison.Ordinal)
            .Replace("hosts: [localhost, 127.0.0.1]", "hosts: [*key]", StringComparison.Ordinal);
        var error = Assert.Throws<ConfigurationException>(() => Loader(_ => "secret-that-must-stay-private").LoadText(yaml));
        Assert.DoesNotContain("secret-that-must-stay-private", error.ToString());
        Assert.Contains("aliases", error.Message);
    }

    [Fact]
    public void ConfigIncomingRootCannotContainTheDataRoot()
        => Assert.Throws<ConfigurationException>(() => Loader().LoadText(Valid.Replace($"incoming_root: {Incoming}", $"incoming_root: {root}", StringComparison.Ordinal)));

    [Fact]
    public void SettingsUpdatesRejectStaleInvalidAndUnauthorizedCandidatesWithoutChangingTheFile()
    {
        var path = Path.Combine(root, "settings.yaml");
        File.WriteAllText(path, Valid);
        using var configuration = new ReloadingConfiguration(path, Loader(), watch: false);
        var first = configuration.ReadForEdit();
        Assert.Throws<ConfigurationEditAuthorityException>(() => configuration.UpdateForEdit(first.Version, value => value.Replace("model: chat", "model: next", StringComparison.Ordinal), () => false));
        Assert.Equal(Valid, File.ReadAllText(path)); Assert.Same(first.Options, configuration.CurrentValue);
        Assert.Throws<ConfigurationException>(() => configuration.UpdateForEdit(first.Version, value => value.Replace("port: 7171", "port: 7222", StringComparison.Ordinal), () => true));
        Assert.Equal(Valid, File.ReadAllText(path));
        var updated = configuration.UpdateForEdit(first.Version, value => value.Replace("model: chat", "model: next", StringComparison.Ordinal), () => true);
        Assert.Equal("next", updated.Options.Models.Chat!.Model); Assert.NotEqual(first.Version, updated.Version);
        Assert.Throws<ConfigurationEditConflictException>(() => configuration.UpdateForEdit(first.Version, value => value, () => true));
        Assert.Equal("next", Loader().Load(path).Models.Chat!.Model);
    }

    [Fact]
    public void SettingsUpdateDetectsExternalEditsAndPreservesSymlinkAndPermissions()
    {
        var path = Path.Combine(root, "settings-target.yaml"); var link = Path.Combine(root, "settings-link.yaml");
        File.WriteAllText(path, Valid); File.CreateSymbolicLink(link, path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var configuration = new ReloadingConfiguration(link, Loader(), watch: false);
        var first = configuration.ReadForEdit();
        File.WriteAllText(path, Valid + "\n# external edit\n");
        Assert.Throws<ConfigurationEditConflictException>(() => configuration.UpdateForEdit(first.Version, value => value, () => true));
        var latest = configuration.ReadForEdit();
        configuration.UpdateForEdit(latest.Version, value => value.Replace("model: chat", "model: next", StringComparison.Ordinal), () => true);
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Contains("model: next", File.ReadAllText(path));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Empty(Directory.GetFiles(root, ".secondbrain-settings-*"));
    }

    [Fact]
    public async Task WatcherFollowsConfigurationLinkWhenItsTargetIsAtomicallyReplaced()
    {
        var directory = Path.Combine(root, "writable-settings"); Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "config.yaml"); var link = Path.Combine(root, "config-link.yaml");
        File.WriteAllText(target, Valid); File.CreateSymbolicLink(link, target);
        using var configuration = new ReloadingConfiguration(link, Loader());
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        configuration.Changed += value => { if (value.Models.Chat!.Model == "updated") changed.TrySetResult(); };
        var replacement = Path.Combine(directory, "replacement.yaml");
        File.WriteAllText(replacement, Valid.Replace("model: chat", "model: updated", StringComparison.Ordinal));
        File.Move(replacement, target, overwrite: true);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("updated", configuration.CurrentValue.Models.Chat!.Model);
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void ConfigReentrantReloadDoesNotDeliverSupersededSnapshotLast()
    {
        var path = Path.Combine(root, "config.yaml");
        File.WriteAllText(path, Valid);
        using var source = new ReloadingConfiguration(path, Loader(), watch: false);
        var observed = new List<string>();
        source.Changed += value =>
        {
            if (value.Models.Chat!.Model != "first") return;
            File.WriteAllText(path, Valid.Replace("model: chat", "model: second", StringComparison.Ordinal));
            Assert.True(source.TryReload());
        };
        source.Changed += value => observed.Add(value.Models.Chat!.Model);
        File.WriteAllText(path, Valid.Replace("model: chat", "model: first", StringComparison.Ordinal));
        Assert.True(source.TryReload());
        Assert.Equal("second", Assert.Single(observed));
        Assert.Equal("second", source.CurrentValue.Models.Chat!.Model);
    }

    [Fact]
    public async Task ConfigConcurrentReloadsDeliverChangesInPublicationOrder()
    {
        var path = Path.Combine(root, "config.yaml");
        File.WriteAllText(path, Valid);
        using var source = new ReloadingConfiguration(path, Loader(), watch: false);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var observed = new List<string>();
        source.Changed += value =>
        {
            if (value.Models.Chat!.Model != "first") return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test subscriber was not released.");
        };
        source.Changed += value => observed.Add(value.Models.Chat!.Model);
        File.WriteAllText(path, Valid.Replace("model: chat", "model: first", StringComparison.Ordinal));
        var first = Task.Run(source.TryReload);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        File.WriteAllText(path, Valid.Replace("model: chat", "model: second", StringComparison.Ordinal));
        var second = Task.Run(source.TryReload);
        try { await Task.Delay(100); }
        finally { release.Set(); }
        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(new[] { "first", "second" }, observed);
        Assert.Equal("second", source.CurrentValue.Models.Chat!.Model);
    }

    [Theory]
    [InlineData("unknown: value")]
    [InlineData("auth: null")]
    [InlineData("privacy: null")]
    [InlineData("server: { listeners: null }")]
    [InlineData("privacy: { trusted_services: null }")]
    [InlineData("sources: { allowed_roots: null }")]
    [InlineData("data_root: /one\ndata_root: /two")]
    [InlineData("---\n{}\n---\n{}")]
    [InlineData("server: [")]
    public void ConfigRejectsInvalidShapeWithoutSourceValues(string yaml)
        => Assert.Throws<ConfigurationException>(() => Loader().LoadText(yaml));

    [Fact]
    public void ConfigInvalidReloadKeepsSameRunningSnapshotAndLogsReason()
    {
        var path = Path.Combine(root, "config.yaml");
        File.WriteAllText(path, Valid);
        var diagnostics = new List<string>();
        using var source = new ReloadingConfiguration(path, Loader(), diagnostics.Add, false);
        var previous = source.CurrentValue;
        var changes = 0;
        source.Changed += _ => changes++;
        File.WriteAllText(path, Valid.Replace("provider: local", "provider: missing", StringComparison.Ordinal));
        Assert.False(source.TryReload());
        Assert.Same(previous, source.CurrentValue);
        Assert.Equal(0, changes);
        Assert.Contains("declared provider", Assert.Single(diagnostics));
    }

    [Fact]
    public void ConfigValidReloadNotifiesSubscribersAndRestartChangesAreRejected()
    {
        var path = Path.Combine(root, "config.yaml");
        File.WriteAllText(path, Valid);
        using var source = new ReloadingConfiguration(path, Loader(), watch: false);
        SecondBrainOptions? notified = null;
        source.Changed += value => notified = value;
        File.WriteAllText(path, Valid.Replace("model: chat", "model: next", StringComparison.Ordinal));
        Assert.True(source.TryReload());
        Assert.Same(source.CurrentValue, notified);
        File.WriteAllText(path, Valid.Replace("port: 7171", "port: 7443", StringComparison.Ordinal));
        Assert.False(source.TryReload());
        Assert.Equal("next", source.CurrentValue.Models.Chat!.Model);
    }

    [Fact]
    public async Task ConfigAtomicFileReplacementTriggersWatch()
    {
        var path = Path.Combine(root, "config.yaml");
        File.WriteAllText(path, Valid);
        using var source = new ReloadingConfiguration(path, Loader());
        var changed = new TaskCompletionSource<SecondBrainOptions>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Changed += value => changed.TrySetResult(value);
        File.WriteAllText(path + ".new", Valid.Replace("model: chat", "model: changed", StringComparison.Ordinal));
        File.Move(path + ".new", path, true);
        var result = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("changed", result.Models.Chat!.Model);
    }

    [Fact]
    public async Task ConfigSighupPublishesAValidatedSnapshotWithoutAFileEvent()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(root, "config.yaml");
        File.WriteAllText(path, Valid);
        using var source = new ReloadingConfiguration(path, Loader());
        var original = source.CurrentValue;
        var changed = new TaskCompletionSource<SecondBrainOptions>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Changed += value => changed.TrySetResult(value);
        Assert.Equal(0, Kill(Environment.ProcessId, 1));
        var result = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(original, result);
        Assert.Equal(original.Models.Chat!.Model, result.Models.Chat!.Model);
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    public void Dispose() => Directory.Delete(root, true);
}
