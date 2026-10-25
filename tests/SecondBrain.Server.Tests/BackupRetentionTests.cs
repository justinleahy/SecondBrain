using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Operations;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Http;
using System.Text.Json;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class BackupRetentionTests
{
    [Fact]
    public async Task RotationIsBoundedAndRetainsNewestCompleteArchiveDuringPartialFailures()
    {
        await using var factory = new LaneDWebFactory(options => options.Backup.Keep = 2);
        var backups = factory.Services.GetRequiredService<IBackupOperations>();
        var directory = Folder(factory);
        var complete = await Add(directory, 1);
        for (var i = 2; i <= 100; i++) await Add(directory, i, partial: true);
        var first = await backups.RotateAsync();
        Assert.Equal(64, first.Deleted); Assert.True(first.MoreWork);
        var second = await backups.RotateAsync();
        Assert.False(second.MoreWork); Assert.Equal(3, Directory.GetFiles(directory).Length);
        Assert.True(File.Exists(complete)); Assert.True(File.Exists(Name(directory, 99, true))); Assert.True(File.Exists(Name(directory, 100, true)));
        factory.Options.CurrentValue.Backup.Keep = 1;
        await backups.RotateAsync();
        Assert.Equal(2, Directory.GetFiles(directory).Length); Assert.True(File.Exists(complete));
    }

    [Fact]
    public async Task QuarantinedArchivesRecoverBeforePolicyRunsAndUnrelatedFilesAreUntouched()
    {
        await using var factory = new LaneDWebFactory(options => options.Backup.Keep = 1);
        var backups = factory.Services.GetRequiredService<IBackupOperations>(); var directory = Folder(factory);
        var old = await Add(directory, 1); var latest = await Add(directory, 2);
        File.Move(latest, Path.Combine(directory, ".pruning-" + Path.GetFileName(latest)));
        var foreign = Name(directory, 999, false); await File.WriteAllTextAsync(foreign, "unrelated file");
        var outside = Path.Combine(factory.Options.CurrentValue.DataRoot, "outside"); await File.WriteAllTextAsync(outside, "unchanged");
        var link = Name(directory, 998, false); File.CreateSymbolicLink(link, outside);
        var result = await backups.RotateAsync();
        Assert.Equal(1, result.Deleted); Assert.False(File.Exists(old)); Assert.True(File.Exists(latest));
        Assert.Equal("unrelated file", await File.ReadAllTextAsync(foreign)); Assert.Equal("unchanged", await File.ReadAllTextAsync(outside));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public async Task RetentionSettingsReloadWithoutChangingKeyOrSourceConfiguration()
    {
        await using var factory = new LaneDWebFactory(); await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password")); await Csrf(client);
        using var step = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password")); await Csrf(client);
        var before = (await client.GetFromJsonAsync<SettingsSnapshot>("/configuration"))!;
        var key = factory.Options.CurrentValue.Backup.KeyFile;
        using var rejected = await client.PutAsJsonAsync("/configuration", new SettingsChange(before.Version, "backup", JsonSerializer.SerializeToElement(new BackupRetentionSetting(0))));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejected.StatusCode);
        using var saved = await client.PutAsJsonAsync("/configuration", new SettingsChange(before.Version, "backup", JsonSerializer.SerializeToElement(new BackupRetentionSetting(3)))); saved.EnsureSuccessStatusCode();
        Assert.Equal(3, (await saved.Content.ReadFromJsonAsync<SettingsSnapshot>())!.Backup.Keep);
        Assert.Equal(3, factory.Options.CurrentValue.Backup.Keep); Assert.Equal(key, factory.Options.CurrentValue.Backup.KeyFile);
    }

    [Fact]
    public async Task ManualAndHourlyRetentionApplyTheCurrentArchivePolicy()
    {
        await using var factory = new LaneDWebFactory(options => options.Backup.Keep = 1);
        await factory.SeedAccountAsync(); var directory = Folder(factory);
        var first = await Add(directory, 1); await Add(directory, 2);
        using var client = factory.CreatePrivateClient(); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest("test-password"));
        await Csrf(client);
        using var denied = await client.PostAsJsonAsync("/maintenance/retention", new { });
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode); Assert.True(File.Exists(first));
        using var step = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password")); await Csrf(client);
        using var applied = await client.PostAsJsonAsync("/v1/maintenance/retention", new { }); applied.EnsureSuccessStatusCode();
        var result = await applied.Content.ReadFromJsonAsync<SecondBrain.Core.Ingestion.RetentionResult>(); Assert.Equal(1, result!.Backups!.Deleted);
        var second = Name(directory, 2, false); await Add(directory, 3);
        factory.Clock.Advance(TimeSpan.FromHours(1));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (File.Exists(second) && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.False(File.Exists(second)); Assert.True(File.Exists(Name(directory, 3, false)));
    }

    private static string Folder(LaneDWebFactory factory)
    {
        var path = Path.Combine(factory.Options.CurrentValue.DataRoot, "backups"); Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
    private static string Name(string directory, int ordinal, bool partial) => Path.Combine(directory,
        "backup-" + new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(ordinal).ToString("yyyyMMddTHHmmssfffffffZ", System.Globalization.CultureInfo.InvariantCulture)
        + "-0123456789abcdef0123456789abcdef" + (partial ? ".partial" : "") + ".sbk");
    private static async Task<string> Add(string directory, int ordinal, bool partial = false)
    {
        // Retention only inspects the public magic; encryption/manifest integrity has separate coverage.
        var path = Name(directory, ordinal, partial); await File.WriteAllBytesAsync(path, "SBKB0001archive-retention-fixture"u8.ToArray()); return path;
    }
    private static async Task Csrf(HttpClient client)
    { var csrf = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!; client.DefaultRequestHeaders.Remove(csrf.HeaderName); client.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token); }
}
