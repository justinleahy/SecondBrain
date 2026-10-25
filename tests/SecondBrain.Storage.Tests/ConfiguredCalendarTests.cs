using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Sources;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Sources;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class ConfiguredCalendarTests
{
    private static readonly IngestionAuthority Actor = new("configured-admin", 1, 1, "admin", "session");
    [Fact]
    public async Task RestartPreservesSourceContainerEnvelopeAndOutstandingLease()
    {
        using var root = new MigrationTestDataRoot();
        string? container; CalendarRefreshWork lease;
        var settings = Settings(); var resolver = new Resolver();
        await using (var stores = new MigrationTestStores(root.Path))
        {
            await stores.Runner.MigrateAsync(); var repository = new SqliteCalendarSubscriptionRepository(stores.State, TimeProvider.System);
            await Service(settings, resolver, repository).InitializeAsync();
            container = (await repository.GetAsync("shared"))!.ContainerId;
            lease = Assert.Single(await repository.ClaimDueAsync());
            Assert.DoesNotContain("private-test-token", await ConfigurationText(stores));
        }
        await using var restarted = new MigrationTestStores(root.Path); await restarted.Runner.MigrateAsync();
        var current = new SqliteCalendarSubscriptionRepository(restarted.State, TimeProvider.System);
        await Service(settings, resolver, current).InitializeAsync();
        Assert.Equal(container, (await current.GetAsync("shared"))!.ContainerId);
        Assert.Empty(await current.ClaimDueAsync());
        Assert.True(await current.IsCurrentAsync(lease.SourceId, lease.Generation));
        Assert.Equal(lease.ProtectedUrl, Assert.Single(await current.ListConfiguredAsync()).ProtectedUrl);
        Assert.DoesNotContain("private-test-token", JsonSerializer.Serialize(await current.ListConfiguredAsync()));
        Assert.DoesNotContain("private-test-token", Assert.Single(await current.ListConfiguredAsync()).ToString());
    }
    [Fact]
    public async Task ChangedSecretRejectsTheWholeConfigurationBatchBeforeAnyUpdates()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await stores.Runner.MigrateAsync();
        var settings = Settings(); var resolver = new Resolver(); var repository = new SqliteCalendarSubscriptionRepository(stores.State, TimeProvider.System);
        var service = Service(settings, resolver, repository); await service.InitializeAsync();
        settings.Sources.CalendarSubscriptions.Insert(0, new() { Id = "new", Name = "New", UrlReference = "${OTHER}" });
        settings.Sources.CalendarSubscriptions[1].Name = "Must not apply";
        resolver.Url = "https://feed.test/private-test-token-changed";
        var error = await Assert.ThrowsAsync<ConfigurationException>(async () => await service.InitializeAsync());
        Assert.DoesNotContain("private-test-token", error.Message);
        Assert.Equal("Shared", (await repository.GetAsync("shared"))!.Name); Assert.Null(await repository.GetAsync("new"));
    }
    [Fact]
    public async Task OmittingConfiguredSourcePausesAndFencesItButLeavesApiSourcesActive()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        var settings = Settings(); var repository = new SqliteCalendarSubscriptionRepository(stores.State, TimeProvider.System);
        var service = Service(settings, new Resolver(), repository); await service.InitializeAsync();
        var configured = Assert.Single(await repository.ClaimDueAsync());
        var api = await repository.CreateAsync("API", "https://feed.test", "api-envelope", 3600, true, Actor);
        settings.Sources.CalendarSubscriptions.Clear(); await service.InitializeAsync();
        Assert.False(await repository.IsCurrentAsync(configured.SourceId, configured.Generation));
        Assert.Equal("paused", (await repository.GetAsync("shared"))!.Freshness);
        Assert.Equal("active", (await repository.GetAsync(api.Id))!.Status);
        settings.Sources.CalendarSubscriptions.Add(new() { Id = "shared", Name = "Resumed", UrlReference = "${LINK}" });
        await service.InitializeAsync();
        Assert.Equal(configured.ContainerId, (await repository.GetAsync("shared"))!.ContainerId);
        Assert.Equal("Resumed", (await repository.GetAsync("shared"))!.Name);
    }
    [Fact]
    public async Task ApiEditsKeepConfiguredOwnershipAndRestartReconcilesDeclaredSettings()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        var settings = Settings(); var repository = new SqliteCalendarSubscriptionRepository(stores.State, TimeProvider.System);
        var service = Service(settings, new Resolver(), repository); await service.InitializeAsync();
        await repository.UpdateAsync("shared", "Temporary", 7200, false, Actor);
        Assert.Single(await repository.ListConfiguredAsync());
        await service.InitializeAsync();
        var source = (await repository.GetAsync("shared"))!;
        Assert.Equal("Shared", source.Name); Assert.Equal(3600, source.PollIntervalSeconds); Assert.Equal("active", source.Status);
    }
    [Fact]
    public async Task ExistingNonconfiguredIdConflictRollsBackEveryRegistration()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        var repository = new SqliteCalendarSubscriptionRepository(stores.State, TimeProvider.System);
        var existing = await repository.CreateAsync("API", "https://feed.test", "api-envelope", 3600, false, Actor);
        var settings = Settings(); settings.Sources.CalendarSubscriptions.Add(new() { Id = existing.Id, Name = "Collision", UrlReference = "${LINK}" });
        await Assert.ThrowsAsync<ConfigurationException>(async () => await Service(settings, new Resolver(), repository).InitializeAsync());
        Assert.Null(await repository.GetAsync("shared")); Assert.Empty(await repository.ListConfiguredAsync());
    }
    [Fact]
    public async Task BatchAdmissionLimitFailureLeavesNoPartialSources()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await stores.Runner.MigrateAsync();
        var settings = Settings(); settings.Limits.Global.QueuedJobs = 1;
        settings.Sources.CalendarSubscriptions.Add(new() { Id = "second", Name = "Second", UrlReference = "${OTHER}" });
        var repository = new SqliteCalendarSubscriptionRepository(stores.State, TimeProvider.System, new Monitor(settings));
        await Assert.ThrowsAsync<ConfigurationException>(async () => await Service(settings, new Resolver(), repository).InitializeAsync());
        Assert.Empty(await repository.ListAsync());
    }
    [Fact]
    public async Task ARemovedConfiguredSourceIsNotRecreatedOnRestart()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        var repository = new SqliteCalendarSubscriptionRepository(stores.State, TimeProvider.System,
            publications: new SecondBrain.Storage.Durability.PublicationCoordinator(stores.State, stores.Index));
        var settings = Settings(); var service = Service(settings, new Resolver(), repository);
        await service.InitializeAsync();
        await repository.RemoveAsync("shared", Actor);
        await service.InitializeAsync();
        Assert.Null(await repository.GetAsync("shared")); Assert.Empty(await repository.ListConfiguredAsync());
        Assert.Empty(await repository.ClaimDueAsync());
    }
    [Fact]
    public async Task ChangingAnIdleActiveSourceHonorsTheQueueLimit()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        var settings = Settings(); settings.Limits.Global.QueuedJobs = 1;
        var repository = new SqliteCalendarSubscriptionRepository(stores.State, TimeProvider.System, new Monitor(settings));
        var service = Service(settings, new Resolver(), repository); await service.InitializeAsync();
        var work = Assert.Single(await repository.ClaimDueAsync());
        await repository.FailAsync(work, "calendar_timeout");
        await repository.CreateAsync("Other", "https://feed.test", "other-envelope", 3600, true, Actor);
        settings.Sources.CalendarSubscriptions[0].Name = "Renamed";
        await Assert.ThrowsAsync<ConfigurationException>(async () => await service.InitializeAsync());
        Assert.Equal("Shared", (await repository.GetAsync("shared"))!.Name);
    }
    private static ConfiguredCalendarSubscriptions Service(SecondBrainOptions settings, Resolver resolver, ICalendarSubscriptionRepository repository) => new(new Monitor(settings), resolver, new Protection(), repository);
    private static SecondBrainOptions Settings() => new() { Sources = new() { CalendarAllowedOrigins = ["https://feed.test"], CalendarSubscriptions = [new() { Id = "shared", Name = "Shared", UrlReference = "${LINK}" }] } };
    private static async Task<string> ConfigurationText(MigrationTestStores stores) => (string)(await MigrationTestSql.ScalarAsync(stores.State, "SELECT config_json FROM sources WHERE id='shared'"))!;
    private static async Task Seed(MigrationTestStores stores)
    {
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
        await new AuthRepository(stores.State, TimeProvider.System).AddAsync(new CredentialRecord { Id = Actor.CredentialId, Name = "admin", Kind = "session", Generation = 1, AccountEpoch = 1, Scopes = "read,write,admin", Verifier = "fixture", Kid = "fixture", CreatedAt = AuthTime.Format(DateTimeOffset.UtcNow) });
    }
    private sealed class Resolver : ISecretResolver
    {
        internal string Url = "webcal://feed.test/private-test-token";
        public string Resolve(string reference) => reference == "${LINK}" ? Url : "https://feed.test/other-private-test-token";
    }
    private sealed class Protection : ICalendarSubscriptionSecrets
    {
        public string Protect(string url) => Convert.ToBase64String(Encoding.UTF8.GetBytes(url));
        public string Unprotect(string envelope) => Encoding.UTF8.GetString(Convert.FromBase64String(envelope));
    }
    private sealed class Monitor(SecondBrainOptions value) : IOptionsMonitor<SecondBrainOptions>
    {
        public SecondBrainOptions CurrentValue => value;
        public SecondBrainOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<SecondBrainOptions, string?> listener) => null;
    }
}
