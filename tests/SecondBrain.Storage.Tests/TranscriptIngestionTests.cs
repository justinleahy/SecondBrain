using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Chat;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using SecondBrain.Storage.Retrieval;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class TranscriptIngestionTests
{
    private static readonly IngestionAuthority Actor = new("transcript-session", 1, 1, "transcript fixture", "session");

    [Theory]
    [InlineData("Ada", " ADA ")]
    [InlineData("Áda", " ÁDA ")]
    public async Task ImportSearchRechunkRebuildAndTrashPreserveSpeakerLocators(string speaker, string filter)
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var coordinator = new PublicationCoordinator(stores.State, stores.Index);
        var documents = new SqliteDocumentRepository(stores.State, stores.Index, coordinator, files);
        const string body = "Pricing is too high.\nPricing is excellent.";
        var metadata = new CaptureRequest("", "Pricing meeting", Type: "transcript", Props: JsonSerializer.SerializeToElement(new { participants = new[] { "ada@example.com", "bob@example.com" }, started_at = "2026-10-24T10:00:00Z" }));
        var admitted = await documents.AdmitFileAsync(Encoding.UTF8.GetBytes("original vtt bytes"), metadata, "meeting.vtt", Actor);
        var extraction = new ExtractionResult("extracted", "vtt", "text/vtt", body, Segments: [new(0, speaker, 1000, 2000, 0, 20), new(1, "Bob", 1500, 3000, 21, 21)]);
        var completed = await documents.CompleteFileAsync(admitted.Document.Id, 1, extraction);
        Assert.Equal("transcript", completed.Document.Type);
        Assert.Equal(2, completed.Document.Segments!.Count);
        Assert.Equal(2L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM segments"));
        var retrieval = new SqliteRetrievalRepository(stores.Index, new Protection(), state: stores.State);
        var filtered = new RetrievalFilters(Type: "transcript", People: [new(filter, "speaker")]);
        var hit = Assert.Single((await retrieval.SearchAsync(new("pricing", "keyword", filtered))).Results);
        Assert.Equal("Pricing is too high.", hit.Snippet);
        Assert.Equal(new(0, 0, speaker, 1000, 2000), hit.Transcript);
        var chat = new SqliteChatRepository(stores.State, TimeProvider.System);
        var actor = new AuthenticatedCredential(Actor.CredentialId, Actor.Kind, 1, 1, new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer, Scope.Admin }, AuthTime.Format(DateTimeOffset.UtcNow));
        var turn = await chat.StartAsync(new("What did Ada say about pricing?"), new("local", "fixture"), actor);
        var source = Assert.Single(await chat.ExposeAsync(turn.Turn.Id, [new(hit.DocumentId, hit.Revision, hit.ChunkId)], actor));
        Assert.Equal(hit.Transcript, source.Transcript);
        Assert.Equal(hit.Snippet, source.Excerpt);
        Assert.Equal(completed.Document.Id, Assert.Single((await retrieval.QueryAsync(new(Type: "transcript", People: [new("ADA@EXAMPLE.COM", "participant")]), Actor)).Records).Id);
        var embeddings = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var space = await embeddings.EnsureSpaceAsync("transcript-test", "fixture", "model", 4);
        var pending = Assert.Single(await embeddings.PendingAsync(space.Id));
        Assert.True(await embeddings.SaveAsync(space, pending, pending.Chunks.ToDictionary(chunk => chunk.Id, _ => new[] { 1f, 0f, 0f, 0f })));
        Assert.True(await embeddings.ActivateAsync(space.Id));
        var semantic = Assert.Single((await retrieval.SearchAsync(new("pricing", "semantic", filtered), [1f, 0f, 0f, 0f], space.Id)).Results);
        Assert.Equal(speaker, semantic.Transcript!.Speaker);
        var monitor = new Monitor();
        Assert.Equal(1, await new SqliteChunkingWorkRepository(stores.State, coordinator, documents, monitor).ProcessAsync(new(64, 128, 8)));
        Assert.All((await documents.GetAsync(admitted.Document.Id, true))!.Chunks!, chunk => Assert.NotNull(chunk.Transcript));
        await documents.RebuildAsync();
        Assert.Equal(2L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM segments"));
        var updated = await documents.MutateAsync(admitted.Document.Id, 1, "patch", JsonSerializer.SerializeToElement(new { tags = new[] { "meeting" } }), Actor);
        Assert.Equal(2, updated.Document.Segments!.Count);
        await documents.MutateAsync(admitted.Document.Id, 2, "delete", JsonSerializer.SerializeToElement(new { }), Actor);
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM segments"));
        await documents.MutateAsync(admitted.Document.Id, 3, "restore", JsonSerializer.SerializeToElement(new { }), Actor);
        Assert.Equal(2L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM segments"));
    }

    [Fact]
    public async Task ReclassificationKeepsOriginalFieldsAndRehydratesThemOnReturn()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var documents = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        var article = await documents.CaptureAsync(new("Authored text", "Story", "article", JsonSerializer.SerializeToElement(new { author = "Ada" })), Actor);
        var note = await documents.MutateAsync(article.Document.Id, 1, "patch", JsonSerializer.SerializeToElement(new { type = "note" }), Actor);
        Assert.Equal("{}", note.Document.PropsJson);
        Assert.Contains("Ada", (string)(await MigrationTestSql.ScalarAsync(stores.State, "SELECT props_original_json FROM documents"))!);
        var restored = await documents.MutateAsync(article.Document.Id, 2, "patch", JsonSerializer.SerializeToElement(new { type = "article" }), Actor);
        Assert.Equal("Ada", JsonSerializer.Deserialize<JsonElement>(restored.Document.PropsJson).GetProperty("author").GetString());
    }

    private static async Task Seed(MigrationTestStores stores)
    {
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
        await new AuthRepository(stores.State, TimeProvider.System).AddAsync(new CredentialRecord { Id = Actor.CredentialId, Name = Actor.CredentialName, Kind = Actor.Kind, Generation = 1, AccountEpoch = 1, Scopes = "read,write,infer,admin", Verifier = "fixture", Kid = "fixture", SteppedUpAt = AuthTime.Format(DateTimeOffset.UtcNow), CreatedAt = AuthTime.Format(DateTimeOffset.UtcNow) });
    }
    private sealed class Protection : IPayloadProtection
    {
        public string Protect(string purpose, string plaintext) => plaintext;
        public string Unprotect(string purpose, string protectedText) => protectedText;
    }
    private sealed class Monitor : IOptionsMonitor<SecondBrainOptions>
    {
        public SecondBrainOptions CurrentValue { get; } = new();
        public SecondBrainOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SecondBrainOptions, string?> listener) => null;
    }
}
