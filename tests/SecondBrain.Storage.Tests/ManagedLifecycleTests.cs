using System.Text.Json;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using SecondBrain.Infrastructure.Ingestion;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class ManagedLifecycleTests
{
    private static readonly IngestionAuthority Actor = new("lifecycle-session", 1, 1, "lifecycle fixture", "session");

    [Fact]
    public async Task ManagedEditTrashRestoreAndPurgeJournalBytesAndRejectLateRevision()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path);
        using var files = new ManagedDocumentFiles(root.Path, journal);
        var documents = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        var captured = await documents.CaptureAsync(new("old authored astronomy", "Old title"), Actor);
        var edited = await documents.MutateAsync(captured.Document.Id, 1, "update", JsonSerializer.SerializeToElement(new { content = "updated authored astronomy", title = "Updated" }), Actor);
        Assert.Equal(2, edited.Document.Revision); var originalPath = edited.Document.Path!;
        Assert.Equal("updated authored astronomy", await File.ReadAllTextAsync(originalPath));
        var deleted = await documents.MutateAsync(captured.Document.Id, 2, "delete", JsonSerializer.SerializeToElement(new { }), Actor);
        Assert.Equal(3, deleted.Document.Revision); Assert.False(File.Exists(originalPath));
        var trash = Directory.GetFiles(Path.Combine(root.Path, "trash"), "*.md", SearchOption.AllDirectories); var trashedPath = Assert.Single(trash);
        Assert.Equal("updated authored astronomy", await File.ReadAllTextAsync(trashedPath));
        var restored = await documents.MutateAsync(captured.Document.Id, 3, "restore", JsonSerializer.SerializeToElement(new { }), Actor);
        Assert.Equal(4, restored.Document.Revision); Assert.True(File.Exists(originalPath)); Assert.False(File.Exists(trashedPath));
        Assert.Equal("updated authored astronomy", restored.Document.Text);
        var purged = await documents.MutateAsync(captured.Document.Id, 4, "purge", JsonSerializer.SerializeToElement(new { }), Actor);
        Assert.Equal(5, purged.Document.Revision); Assert.Equal("[purged]", purged.Document.Title); Assert.False(File.Exists(originalPath));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM payloads WHERE redacted_at IS NULL AND instr(CAST(bytes AS TEXT),'updated authored astronomy')>0"));
        await Assert.ThrowsAsync<IngestionConflictException>(() => documents.CompleteFileAsync(captured.Document.Id, 4, new("extracted", "text", "text/plain", "late resurrecting bytes"), default).AsTask());
        await Assert.ThrowsAsync<IngestionConflictException>(() => documents.MutateAsync(captured.Document.Id, 5, "restore", JsonSerializer.SerializeToElement(new { }), Actor).AsTask());
    }
    private static async Task Seed(MigrationTestStores stores)
    {
        await stores.Runner.MigrateAsync(); await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
        await new AuthRepository(stores.State, TimeProvider.System).AddAsync(new CredentialRecord { Id = Actor.CredentialId, Name = Actor.CredentialName, Kind = Actor.Kind, Generation = 1, AccountEpoch = 1, Scopes = "read,write,admin", Verifier = "fixture", Kid = "fixture", SteppedUpAt = AuthTime.Format(DateTimeOffset.UtcNow), CreatedAt = AuthTime.Format(DateTimeOffset.UtcNow) });
    }
}
