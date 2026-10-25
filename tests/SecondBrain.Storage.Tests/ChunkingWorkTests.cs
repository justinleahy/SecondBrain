using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class ChunkingWorkTests
{
    private static readonly IngestionAuthority Actor = new("chunk-session", 1, 1, "chunk fixture", "session");
    private sealed class Monitor : IOptionsMonitor<SecondBrainOptions>
    {
        public SecondBrainOptions CurrentValue { get; set; } = new();
        public SecondBrainOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SecondBrainOptions, string?> listener) => null;
    }
    private static async Task Seed(MigrationTestStores stores)
    {
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
        await new AuthRepository(stores.State, TimeProvider.System).AddAsync(new CredentialRecord { Id = Actor.CredentialId, Name = Actor.CredentialName, Kind = Actor.Kind, Generation = 1, AccountEpoch = 1, Scopes = "read,write,admin", Verifier = "fixture", Kid = "fixture", SteppedUpAt = AuthTime.Format(DateTimeOffset.UtcNow), CreatedAt = AuthTime.Format(DateTimeOffset.UtcNow) });
    }

    [Fact]
    public async Task RechunkPreservesTextRevisionAndMetadataAndRejectsOldVectors()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var coordinator = new PublicationCoordinator(stores.State, stores.Index); var monitor = new Monitor();
        var documents = new SqliteDocumentRepository(stores.State, stores.Index, coordinator, files, optionsMonitor: monitor);
        var text = string.Join(' ', Enumerable.Repeat("Nebula αβγ astronomy", 200));
        var capture = await documents.CaptureAsync(new(text, "Observations", Tags: ["science"], Aliases: ["Stars"]), Actor);
        var embeddings = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var space = await embeddings.EnsureSpaceAsync("test-profile", "fixture", "model", 4);
        var old = Assert.Single(await embeddings.PendingAsync(space.Id));
        var work = new SqliteChunkingWorkRepository(stores.State, coordinator, documents, monitor);
        var profile = new ChunkingProfile(64, 128, 16);
        Assert.Equal(1, await work.ProcessAsync(profile));
        var updated = (await documents.GetAsync(capture.Document.Id, true))!;
        Assert.Equal(capture.Document.Revision, updated.Revision); Assert.Equal(capture.Document.ContentHash, updated.ContentHash);
        Assert.Equal(text, updated.Text); Assert.Equal(capture.Document.UpdatedAt, updated.UpdatedAt);
        Assert.Equal(["science"], updated.Tags!);
        Assert.True(updated.Chunks!.Count > capture.Document.Chunks!.Count);
        Assert.All(updated.Chunks, chunk => Assert.InRange(chunk.TokenCount, 1, 128));
        Assert.False(await embeddings.SaveAsync(space, old, old.Chunks.ToDictionary(chunk => chunk.Id, _ => new[] { 1f, 0f, 0f, 0f })));
        var current = Assert.Single(await embeddings.PendingAsync(space.Id)); Assert.Equal(profile.GenerationJson, current.GenerationJson);
        Assert.Equal(0, await work.ProcessAsync(profile));
        var projectionJson = (string)(await MigrationTestSql.ScalarAsync(stores.State, "SELECT CAST(p.bytes AS TEXT) FROM document_projections d JOIN payloads p ON p.id=d.payload_id"))!;
        Assert.Contains("Stars", projectionJson);
        await documents.MutateAsync(updated.Id, updated.Revision, "purge", JsonSerializer.SerializeToElement(new { }), Actor);
        Assert.Equal(0, await work.ProcessAsync(new()));
        Assert.Equal("purged", (await documents.GetAsync(updated.Id))!.Status);
    }

    [Fact]
    public async Task PendingReservationAndQueueLimitDeferWorkAndInterruptedPublicationRecovers()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        var coordinator = new PublicationCoordinator(stores.State, stores.Index); var monitor = new Monitor();
        var documents = new SqliteDocumentRepository(stores.State, stores.Index, coordinator);
        var capture = await documents.CaptureAsync(new(new string('a', 2000), "Recovery"), Actor);
        var profile = new ChunkingProfile(64, 128, 8);
        var work = new SqliteChunkingWorkRepository(stores.State, coordinator, documents, monitor);
        await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('mutation_document_reservation:'||'" + capture.Document.Id + "','pending')");
        Assert.Equal(0, await work.ProcessAsync(profile));
        await MigrationTestSql.WriteAsync(stores.State, "DELETE FROM meta WHERE key LIKE 'mutation_document_reservation:%'");
        monitor.CurrentValue.Limits.Global.QueuedJobs = 1;
        Assert.Equal(0, await work.ProcessAsync(profile));
        monitor.CurrentValue.Limits.Global.QueuedJobs = 100;
        var crash = new PublicationCoordinator(stores.State, stores.Index, new AfterState());
        await Assert.ThrowsAsync<InjectedFailure>(() => new SqliteChunkingWorkRepository(stores.State, crash, documents, monitor).ProcessAsync(profile).AsTask());
        Assert.Equal("publishing", await MigrationTestSql.ScalarAsync(stores.State, "SELECT publish_state FROM documents"));
        Assert.Equal(1, await work.ProcessAsync(profile));
        Assert.Equal("published", await MigrationTestSql.ScalarAsync(stores.State, "SELECT publish_state FROM documents"));
        Assert.Equal(0, await work.ProcessAsync(profile));
    }

    [Fact]
    public async Task ExcessChunkCountKeepsReadableProjectionAndDoesNotRetryUntilProfileChanges()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        var coordinator = new PublicationCoordinator(stores.State, stores.Index); var monitor = new Monitor();
        var documents = new SqliteDocumentRepository(stores.State, stores.Index, coordinator);
        var capture = await documents.CaptureAsync(new(new string('a', 20000), "Bound"), Actor);
        var work = new SqliteChunkingWorkRepository(stores.State, coordinator, documents, monitor);
        var tooSmall = new ChunkingProfile(16, 32, 15);
        Assert.Equal(0, await work.ProcessAsync(tooSmall)); Assert.Equal(0, await work.ProcessAsync(tooSmall));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT count(*) FROM jobs WHERE kind='chunk' AND error='chunk-count-bound'"));
        Assert.Equal(capture.Document.Text, (await documents.GetAsync(capture.Document.Id, true))!.Text);
        Assert.Equal(1, await work.ProcessAsync(new(128, 256, 16)));
    }

    [Fact]
    public async Task ExtractionUsesItsAdmittedProfileAfterOptionsChange()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var coordinator = new PublicationCoordinator(stores.State, stores.Index); var monitor = new Monitor();
        var documents = new SqliteDocumentRepository(stores.State, stores.Index, coordinator, files, optionsMonitor: monitor);
        var text = new string('a', 2000);
        var admitted = await documents.AdmitFileAsync(Encoding.UTF8.GetBytes(text), new("", "In flight"), "note.txt", Actor);
        monitor.CurrentValue = new() { Chunking = new() { TargetTokens = 64, MaxTokens = 128, OverlapTokens = 8 } };
        var completed = await documents.CompleteFileAsync(admitted.Document.Id, admitted.Document.Revision, new("extracted", "text", "text/plain", text));
        Assert.Equal(new ChunkingProfile().Process(admitted.Document.Id, "In flight", text).Chunks.Count, completed.Document.Chunks!.Count);
        Assert.Equal(1, await new SqliteChunkingWorkRepository(stores.State, coordinator, documents, monitor).ProcessAsync(ChunkingProfile.FromOptions(monitor.CurrentValue.Chunking)));
        Assert.True((await documents.GetAsync(admitted.Document.Id, true))!.Chunks!.Count > completed.Document.Chunks.Count);
    }

    [Fact]
    public void ProfileChangeRetainsIndependentGenerationFields()
    {
        var profile = new ChunkingProfile(64, 128, 8);
        var updated = profile.ApplyToGeneration("{\"source_generation\":7,\"extractor\":3,\"chunker\":2}");
        using var json = JsonDocument.Parse(updated);
        Assert.Equal(7, json.RootElement.GetProperty("source_generation").GetInt32());
        Assert.Equal(3, json.RootElement.GetProperty("extractor").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("chunker").GetInt32());
        Assert.Equal(profile, ChunkingProfile.FromGeneration(updated));
        var legacy = EmbeddingFingerprint.Create("provider", "http://localhost", "model", 4);
        var changed = EmbeddingFingerprint.Create("provider", "http://localhost", "model", 4, profile);
        Assert.NotEqual(legacy, changed); Assert.Equal(legacy, EmbeddingFingerprint.BindingPart(changed));
        Assert.Equal(profile, EmbeddingFingerprint.Profile(changed));
        Assert.NotEqual(1, EmbeddingFingerprint.ChunkGeneration(changed));
    }

    [Fact]
    public async Task NewEmbeddingSpaceCannotActivateBeforeMatchingChunkGeneration()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        var coordinator = new PublicationCoordinator(stores.State, stores.Index); var monitor = new Monitor();
        var documents = new SqliteDocumentRepository(stores.State, stores.Index, coordinator);
        await documents.CaptureAsync(new(new string('x', 2000), "Space"), Actor);
        var embeddings = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var profile = new ChunkingProfile(64, 128, 8);
        var space = await embeddings.EnsureSpaceAsync(EmbeddingFingerprint.Create("provider", "http://localhost", "model", 4, profile), "provider", "model", 4);
        var old = Assert.Single(await embeddings.PendingAsync(space.Id));
        Assert.True(await embeddings.SaveAsync(space, old, old.Chunks.ToDictionary(chunk => chunk.Id, _ => new[] { 1f, 0f, 0f, 0f })));
        Assert.False(await embeddings.ActivateAsync(space.Id));
        Assert.Equal(1, await new SqliteChunkingWorkRepository(stores.State, coordinator, documents, monitor).ProcessAsync(profile));
        var current = Assert.Single(await embeddings.PendingAsync(space.Id));
        Assert.True(await embeddings.SaveAsync(space, current, current.Chunks.ToDictionary(chunk => chunk.Id, _ => new[] { 1f, 0f, 0f, 0f })));
        Assert.True(await embeddings.ActivateAsync(space.Id));
        Assert.Equal(space.Id, (await embeddings.ActiveAsync())!.Id);
    }

    private sealed class InjectedFailure : Exception;
    private sealed class AfterState : ICrashPoints
    {
        public ValueTask HitAsync(string name, CancellationToken cancellationToken = default) =>
            name == CrashPointNames.AfterStatePublishing ? throw new InjectedFailure() : ValueTask.CompletedTask;
    }
}
