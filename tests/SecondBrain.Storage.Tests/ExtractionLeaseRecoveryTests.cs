using System.Text;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Ingestion;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class ExtractionLeaseRecoveryTests
{
    [Fact]
    public async Task RestartImmediatelyReclaimsUnexpiredExtractionLeaseWithoutDuplicateAdmission()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
        var actor = new IngestionAuthority("lease-recovery", 1, 1, "lease fixture", "api_key");
        await new AuthRepository(stores.State, TimeProvider.System).AddAsync(new CredentialRecord
        {
            Id = actor.CredentialId, Name = actor.CredentialName, Kind = actor.Kind, AccountEpoch = 1, Generation = 1,
            Scopes = "read,write", Verifier = "fixture", Kid = "fixture", CreatedAt = AuthTime.Format(DateTimeOffset.UtcNow)
        });
        using var journal = new MutationJournal(stores.State, root.Path);
        using var files = new ManagedDocumentFiles(root.Path, journal);
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        var admitted = await repository.AdmitFileAsync(Encoding.UTF8.GetBytes("durable extraction"), new CaptureRequest(""), "leased.txt", actor);
        var first = Assert.Single(await repository.PendingFilesAsync());
        Assert.Empty(await repository.PendingFilesAsync());
        // Simulate the next process after its exclusive data-root lock is acquired.
        var restarted = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        await restarted.RecoverFileLeasesAsync();
        await restarted.RecoverFileLeasesAsync();
        var recovered = Assert.Single(await restarted.PendingFilesAsync());
        Assert.Equal((first.JobId, first.DocumentId, first.Revision), (recovered.JobId, recovered.DocumentId, recovered.Revision));
        Assert.Equal(admitted.Document.Id, recovered.DocumentId);
        Assert.Equal(2, (await restarted.JobAsync(recovered.JobId))!.Attempts);
        Assert.Single(await restarted.JobsAsync());
    }
}
