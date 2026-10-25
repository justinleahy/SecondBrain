using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SecondBrain.Cli;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Infrastructure.Security;
using SecondBrain.Storage;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Backup;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using SecondBrain.Storage.Sources;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class CliRestoreTests
{
    private static readonly PasswordParameters Parameters = new(1, 1024, 1, 1);
    [Fact]
    public async Task OfflineRestoreKeepsTheCapturedRevisionRebuildsIndexAndRecoversRealCalendarKeys()
    {
        using var source = new CliTempRoot(); source.CreateRoots();
        using var target = new CliTempRoot(); target.CreateRoots();
        var keyPath = Path.Combine(source.Path, "backup.key");
        var key = RandomNumberGenerator.GetBytes(32); await File.WriteAllBytesAsync(keyPath, key);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var sourceOptions = OptionsFor(source, keyPath); var targetOptions = OptionsFor(target, keyPath);
        var sourceServices = Services(sourceOptions); var targetServices = Services(targetOptions);
        var first = await Run(source, sourceServices, "init", "--json"); Assert.Equal(0, first.Exit);
        Assert.Equal(0, (await Run(target, targetServices, "init", "--json")).Exit);
        string noteId, envelope, calendarId; byte[] signature; string kid;
        var archivePath = Path.Combine(source.Path, "archive.sbk");
        using (var ring = sourceServices.CreateKeyRing(source.Data))
        {
            kid = ring.ActiveKid; signature = ring.Sign(kid, "saved signature"u8);
            envelope = ring.DataProtectionProvider.CreateProtector("restore-calendar-test").Protect("synthetic calendar URL");
        }
        using (var state = new SqliteStateStore(source.Data))
        using (var index = new SqliteIndexStore(source.Data))
        using (var journal = new MutationJournal(state, source.Data))
        using (var files = new ManagedDocumentFiles(source.Data, journal))
        {
            var auth = new AuthRepository(state, TimeProvider.System);
            var credential = Assert.Single((await auth.ListPageAsync("api_key", false, null, 100)).Items);
            var session = new CredentialRecord { Id = "restore-edit-session", Name = "restore editing", Kind = "session", Generation = 1,
                AccountEpoch = credential.AccountEpoch, Scopes = "read,write,admin", Verifier = "test", Kid = credential.Kid,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"), SteppedUpAt = DateTimeOffset.UtcNow.ToString("O") };
            await auth.AddAsync(session);
            var actor = new IngestionAuthority(session.Id, session.Generation, session.AccountEpoch, session.Name, session.Kind);
            var coordinator = new PublicationCoordinator(state, index);
            var documents = new SqliteDocumentRepository(state, index, coordinator, files, Options.Create(sourceOptions));
            noteId = (await documents.CaptureAsync(new("Captured before edits", "Restore checkpoint"), actor)).Document.Id;
            calendarId = (await new SqliteCalendarSubscriptionRepository(state, TimeProvider.System, publications: coordinator, files: files)
                .CreateAsync("Protected calendar", "https://calendar.example.invalid", envelope, 3600, true, actor)).Id;
            await File.WriteAllTextAsync(Path.Combine(source.Incoming, "external.txt"), "Restored external source");
            await new SqliteSourceRepository(state).AddAsync(new("external", "folder", "External",
                new(source.Incoming, ["**"], [], true, "index", false, null, new Dictionary<string, string>(), "poll", 3600), "disabled", null, null, DateTimeOffset.UtcNow));
            Directory.CreateDirectory(Path.Combine(source.Data, "trash"));
            await using var pins = await BackupFilePins.CaptureAsync(journal, state, source.Data,
                [new("vault", Path.Combine(source.Data, "vault")), new("trash", Path.Combine(source.Data, "trash")), new("keyring", Path.Combine(source.Data, "keyring")), new("external/0", source.Incoming)]);
            var metadata = await BackupPackage.DescribeAsync(pins, 1, []);
            async Task Archive()
            {
                await using var output = File.Create(archivePath);
                await BackupPackage.WriteAsync(pins, output, key, metadata);
            }
            async Task Edit()
            {
                for (var i = 0; i < 16; i++)
                    await documents.MutateAsync(noteId, (await documents.GetAsync(noteId))!.Revision, "update",
                        JsonSerializer.SerializeToElement(new { content = "Later edit " + i }), actor);
            }
            await Task.WhenAll(Archive(), Edit());
            Assert.Equal(17, (await documents.GetAsync(noteId))!.Revision);
        }
        using (var existing = new SqliteStateStore(target.Data))
            await existing.QueueWriteAsync(async (c, t, ct) => { await c.ExecuteAsync(new CommandDefinition("UPDATE meta SET value='7' WHERE key='account_epoch'", transaction: t, cancellationToken: ct)); return 0; });
        var refused = await Run(target, targetServices, "maintenance", "restore", archivePath, "--external-root", source.Incoming + "=" + target.Incoming, "--json");
        Assert.Equal(4, refused.Exit);
        Assert.False(File.Exists(Path.Combine(target.Data, Core.Operations.RestoreState.PendingFileName)));
        var destination = Path.Combine(target.Incoming, "restored-source");
        var restored = await Run(target, targetServices, "maintenance", "restore", archivePath, "--external-root", source.Incoming + "=" + destination, "--json");
        Assert.True(restored.Exit == 0, restored.Output + restored.Error);
        Assert.Contains("rotate API and provider keys", restored.Output);
        Assert.Equal("Restored external source", await File.ReadAllTextAsync(Path.Combine(destination, "external.txt")));
        using (var state = new SqliteStateStore(target.Data))
        using (var index = new SqliteIndexStore(target.Data))
        {
            var auth = new AuthRepository(state, TimeProvider.System);
            Assert.Equal(8, await auth.GetEpochAsync());
            Assert.All((await auth.ListPageAsync("api_key", false, null, 100)).Items, credential => Assert.NotNull(credential.RevokedAt));
            await using var read = await state.OpenReadConnectionAsync();
            Assert.Equal(1, await read.Connection.ExecuteScalarAsync<int>("SELECT revision FROM documents WHERE id=@id", new { id = noteId }));
            Assert.Equal(envelope, await read.Connection.ExecuteScalarAsync<string>("SELECT protected_url FROM calendar_subscriptions WHERE source_id=@id", new { id = calendarId }));
            await using var indexed = await index.OpenReadConnectionAsync();
            Assert.Equal("Captured before edits", await indexed.Connection.ExecuteScalarAsync<string>("SELECT lossless_text FROM document_text WHERE document_id=@id", new { id = noteId }));
        }
        using (var ring = targetServices.CreateKeyRing(target.Data))
        {
            Assert.True(ring.Verify("saved signature"u8, signature));
            Assert.Equal("synthetic calendar URL", ring.DataProtectionProvider.CreateProtector("restore-calendar-test").Unprotect(envelope));
            Assert.Contains(kid, ring.Kids);
        }
        Assert.DoesNotContain(envelope, restored.Output); Assert.DoesNotContain("synthetic calendar URL", restored.Output);
    }

    [Fact]
    public async Task RestoreRefusesALockedDaemonAndWrongKeyWithoutChangingExistingAccount()
    {
        using var root = new CliTempRoot(); root.CreateRoots();
        var keyPath = Path.Combine(root.Path, "backup.key"); await File.WriteAllBytesAsync(keyPath, RandomNumberGenerator.GetBytes(32));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var options = OptionsFor(root, keyPath); var services = Services(options);
        Assert.Equal(0, (await Run(root, services, "init", "--json")).Exit);
        var archive = Path.Combine(root.Path, "bad.sbk");
        await using (var output = File.Create(archive))
            await BackupCipher.EncryptAsync(new MemoryStream("archive encrypted under another key"u8.ToArray()), output, RandomNumberGenerator.GetBytes(32));
        using (DataRootLock.Acquire(root.Data)) Assert.Equal(4, (await Run(root, services, "maintenance", "restore", archive, "--json")).Exit);
        Assert.Equal(4, (await Run(root, services, "maintenance", "restore", archive, "--json")).Exit);
        using var state = new SqliteStateStore(root.Data);
        Assert.Equal(1, await new AuthRepository(state, TimeProvider.System).GetEpochAsync());
        Assert.False(File.Exists(Path.Combine(root.Data, Core.Operations.RestoreState.PendingFileName)));
    }

    [Theory]
    [InlineData("maintenance", "restore")]
    [InlineData("maintenance", "restore", "archive", "--rollback")]
    [InlineData("maintenance", "restore", "archive", "--external-root", "relative=/absolute")]
    public async Task InvalidRestoreArgumentsFailBeforeTouchingStorage(params string[] args)
    {
        var output = new StringWriter();
        Assert.Equal(2, await Cli.Program.RunAsync(args, new CliServices(false), output, new StringWriter()));
    }

    private static SecondBrainOptions OptionsFor(CliTempRoot root, string keyPath)
    { var options = root.Options(); options.Backup.KeyFile = keyPath; options.Limits.Global.DiskLowWaterGb = 0; return options; }
    private static CliServices Services(SecondBrainOptions options) => new()
    { ReadConfiguration = (_, _) => options, ReadPassword = (_, _) => Task.FromResult("restore-test-password"), CalibratePassword = () => Parameters };
    private static async Task<(int Exit, string Output, string Error)> Run(CliTempRoot root, CliServices services, params string[] args)
    {
        var output = new StringWriter(); var error = new StringWriter();
        var exit = await Cli.Program.RunAsync(root.Identities(args), services, output, error);
        return (exit, output.ToString(), error.ToString());
    }
}
