using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using SecondBrain.Storage.Retrieval;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class FullScaleCachedRebuildRegressionTests
{
    [Fact]
    public async Task RebuildRestoresCachedVectorsAndOwnersWithoutPendingProviderWork()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO sources(id,kind,name,created_at) VALUES('fixture','api','Fixture','2026-10-09T00:00:00Z')");
        var coordinator = new PublicationCoordinator(stores.State, stores.Index);
        var embeddings = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var space = await embeddings.EnsureSpaceAsync("fixture-fingerprint", "fixture", "fixture-embed", 4);
        var first = TextProcessor.Process("first", "Fixture title", "Observational astronomy and spectroscopy.", path: "/vault/first.md");
        Assert.Equal(PublicationOutcome.Published, await Publish("first", first));
        var work = Assert.Single(await embeddings.PendingAsync(space.Id));
        Assert.True(await embeddings.SaveAsync(space, work, work.Chunks.ToDictionary(chunk => chunk.Id, _ => new float[] { 1, 0, 0, 0 })));
        Assert.True(await embeddings.ActivateAsync(space.Id));
        // A second document sharing an exact embedding input must gain its own cache ownership
        // through publication, even when the embedding processor never handles that document.
        var second = TextProcessor.Process("second", "Fixture title", first.LosslessText, path: "/vault/second.md");
        Assert.Equal(PublicationOutcome.Published, await Publish("second", second));
        Assert.Empty(await embeddings.PendingAsync(space.Id));
        Assert.Equal(2L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM embedding_cache_owners"));
        var repository = new SqliteRetrievalRepository(stores.Index, new UnusedProtection());
        var before = await repository.SearchAsync(new("astronomy", "hybrid"), [1, 0, 0, 0], space.Id);
        var vectorsBefore = await MigrationTestSql.FirstColumnAsync(stores.Index,
            "SELECT c.id||':'||hex(v.vector) FROM chunks c JOIN chunk_vectors v ON v.chunk_rowid=c.rowid ORDER BY c.id");
        var documents = new SqliteDocumentRepository(stores.State, stores.Index, coordinator);
        Assert.Equal(2, await documents.RebuildAsync());
        Assert.Empty(await embeddings.PendingAsync(space.Id));
        var vectorsAfter = await MigrationTestSql.FirstColumnAsync(stores.Index,
            "SELECT c.id||':'||hex(v.vector) FROM chunks c JOIN chunk_vectors v ON v.chunk_rowid=c.rowid ORDER BY c.id");
        Assert.Equal(vectorsBefore, vectorsAfter);
        var after = await repository.SearchAsync(new("astronomy", "hybrid"), [1, 0, 0, 0], space.Id);
        Assert.Equal(before.Results.Select(hit => (hit.ChunkId, hit.DocumentId, hit.Path, hit.Score)),
            after.Results.Select(hit => (hit.ChunkId, hit.DocumentId, hit.Path, hit.Score)));
        Assert.Empty(after.Degraded);
        Assert.Equal(2L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM documents WHERE status='indexed'"));
        // Uncached content remains partial and pending; restoration cannot fabricate coverage.
        var missing = TextProcessor.Process("missing", "Different title", "An uncached glacier observation.");
        Assert.Equal(PublicationOutcome.Published, await Publish("missing", missing));
        Assert.Equal("indexed_partial", await MigrationTestSql.ScalarAsync(stores.State, "SELECT status FROM documents WHERE id='missing'"));
        Assert.Equal("missing", Assert.Single(await embeddings.PendingAsync(space.Id)).DocumentId);

        ValueTask<PublicationOutcome> Publish(string id, TextProjection projection) => coordinator.PublishAsync(
            new DocumentPublication(id, "fixture", 0, "{\"chunker\":1,\"extractor\":1,\"normalization\":1}", "capture", "Fixture title", "fixture-hash", "fixture")
            { Projection = projection, Type = "note", Status = "indexed_partial" });
    }

    private sealed class UnusedProtection : IPayloadProtection
    {
        public string Protect(string purpose, string plaintext) => plaintext;
        public string Unprotect(string purpose, string protectedText) => protectedText;
    }
}
