using System.Text.Json;
using Dapper;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class ClassificationWorkTests
{
    private static readonly IngestionAuthority Actor = new("classification", 1, 1, "classification", "api_key");

    [Fact]
    public async Task CachedDecisionSurvivesStorageRestartAndPublicationKeepsItsProvenance()
    {
        using var root = new MigrationTestDataRoot(); string id; ContentClassification result;
        await using (var stores = new MigrationTestStores(root.Path))
        {
            await Seed(stores); using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
            var documents = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
            id = (await documents.AdmitFileAsync("Original file"u8.ToArray(), new(""), "file.txt", Actor)).Document.Id;
            var repository = new SqliteClassificationWorkRepository(stores.State);
            var work = (await repository.ReadAsync(id, 1))!;
            result = new(work.Stamp, ContentTypeSelection.ConfigurationStamp(new SecondBrainOptions()), work.GenerationJson, "fingerprint", "article", .93, "classified");
            Assert.True(await repository.SaveAsync(work, result));
        }
        await using var restarted = new MigrationTestStores(root.Path); await restarted.Runner.MigrateAsync();
        var cached = new SqliteClassificationWorkRepository(restarted.State);
        Assert.Equal(result, await cached.CachedAsync((await cached.ReadAsync(id, 1))!, "fingerprint"));
        var repo = new SqliteDocumentRepository(restarted.State, restarted.Index, new PublicationCoordinator(restarted.State, restarted.Index));
        var completed = await repo.CompleteClassifiedFileAsync(id, 1, new("extracted", "text", "text/plain", "Original file", Properties:
            new Dictionary<string, JsonElement> { ["author"] = JsonSerializer.SerializeToElement("Ada") }), result);
        Assert.Equal("article", completed.Document.Type); Assert.Contains("Ada", completed.Document.PropsJson);
        await using var read = await restarted.State.OpenReadConnectionAsync();
        Assert.Equal("llm", await read.Connection.ExecuteScalarAsync<string>("SELECT type_origin FROM documents WHERE id=@id", new { id }));
        Assert.Equal(.93, await read.Connection.ExecuteScalarAsync<double>("SELECT type_confidence FROM documents WHERE id=@id", new { id }));
        Assert.Contains("Ada", (await read.Connection.ExecuteScalarAsync<string>("SELECT props_original_json FROM documents WHERE id=@id", new { id }))!);
    }

    [Fact]
    public async Task NewSourceRuleRejectsTheOldCacheAndWinsOverItsPrediction()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var documents = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files);
        var id = (await documents.AdmitFileAsync("Original file"u8.ToArray(), new(""), "file.txt", Actor)).Document.Id;
        var repository = new SqliteClassificationWorkRepository(stores.State);
        var work = (await repository.ReadAsync(id, 1))!;
        var prediction = new ContentClassification(work.Stamp, ContentTypeSelection.ConfigurationStamp(new SecondBrainOptions()), work.GenerationJson, "old", "article", .99, "classified");
        await stores.State.QueueWriteAsync(async (c, t, token) => await c.ExecuteAsync(new CommandDefinition(
            "UPDATE sources SET config_json='{\"default_type\":\"note\",\"type_map\":{}}' WHERE id=(SELECT source_id FROM documents WHERE id=@id)", new { id }, t, cancellationToken: token)));
        Assert.False(await repository.SaveAsync(work, prediction));
        var completed = await documents.CompleteClassifiedFileAsync(id, 1, new("extracted", "text", "text/plain", "Original file"), prediction);
        Assert.Equal("note", completed.Document.Type);
        await using var read = await stores.State.OpenReadConnectionAsync();
        Assert.Equal("source", await read.Connection.ExecuteScalarAsync<string>("SELECT type_origin FROM documents WHERE id=@id", new { id }));
        Assert.Null(await read.Connection.ExecuteScalarAsync<double?>("SELECT type_confidence FROM documents WHERE id=@id", new { id }));
    }

    [Fact]
    public async Task RevokedClassificationConfigurationCannotPublishAnOldModelResult()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await Seed(stores);
        using var journal = new MutationJournal(stores.State, root.Path); using var files = new ManagedDocumentFiles(root.Path, journal);
        var options = new SecondBrainOptions();
        var documents = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index), files,
            Microsoft.Extensions.Options.Options.Create(options));
        var id = (await documents.AdmitFileAsync("Original file"u8.ToArray(), new(""), "file.txt", Actor)).Document.Id;
        var work = (await new SqliteClassificationWorkRepository(stores.State).ReadAsync(id, 1))!;
        var prediction = new ContentClassification(work.Stamp, ContentTypeSelection.ConfigurationStamp(options), work.GenerationJson, "old", "article", .99, "classified");
        options.Classification.Llm = false;
        await Assert.ThrowsAsync<IngestionConflictException>(() => documents.CompleteClassifiedFileAsync(id, 1, new("extracted", "text", "text/plain", "Original file"), prediction).AsTask());
        Assert.Equal("file", (await documents.GetAsync(id))!.Type);
        Assert.Equal("note", (await documents.CompleteFileAsync(id, 1, new("extracted", "text", "text/plain", "Original file"))).Document.Type);
    }

    private static async Task Seed(MigrationTestStores stores)
    {
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
        await new AuthRepository(stores.State, TimeProvider.System).AddAsync(new CredentialRecord
        { Id = Actor.CredentialId, Name = Actor.CredentialName, Kind = Actor.Kind, AccountEpoch = 1, Generation = 1, Scopes = "read,write,infer,admin", Verifier = "fixture", Kid = "fixture", CreatedAt = AuthTime.Format(DateTimeOffset.UtcNow) });
    }
}
