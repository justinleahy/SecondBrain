using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using SecondBrain.Storage.Retrieval;
using Xunit;
using Xunit.Abstractions;

namespace SecondBrain.Storage.Tests;

public sealed class RetrievalEvaluationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SeedRecallAt8Meets085()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Evaluation");
        var documentBytes = await File.ReadAllBytesAsync(Path.Combine(path, "documents.jsonl"));
        var questionBytes = await File.ReadAllBytesAsync(Path.Combine(path, "questions.jsonl"));
        using var recording = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(path, "embeddings.json")));
        var metadata = recording.RootElement;
        Assert.Equal(metadata.GetProperty("documents_sha256").GetString(), Hash(documentBytes));
        Assert.Equal(metadata.GetProperty("questions_sha256").GetString(), Hash(questionBytes));
        var descriptor = metadata.GetProperty("descriptor");
        var dimensions = descriptor.GetProperty("dimensions").GetInt32();
        var vectors = metadata.GetProperty("vectors");
        var documents = Encoding.UTF8.GetString(documentBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<SeedDocument>(line, Json)!).ToArray();
        var questions = Encoding.UTF8.GetString(questionBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<SeedQuestion>(line, Json)!).ToArray();
        Assert.True(documents.Length > 8);

        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
        var actor = new IngestionAuthority("evaluation", 1, 1, "evaluation", "api_key");
        await new AuthRepository(stores.State, TimeProvider.System).AddAsync(new CredentialRecord
        {
            Id = actor.CredentialId, Name = actor.CredentialName, Kind = actor.Kind, AccountEpoch = 1, Generation = 1,
            Scopes = "read,write", Verifier = "fixture", Kid = "fixture", CreatedAt = AuthTime.Format(DateTimeOffset.UtcNow),
        });
        var repository = new SqliteDocumentRepository(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index));
        var embeddings = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var space = await embeddings.EnsureSpaceAsync(metadata.GetProperty("fingerprint").GetString()!, descriptor.GetProperty("provider").GetString()!, descriptor.GetProperty("model").GetString()!, dimensions);
        var identities = new Dictionary<string, string>();
        foreach (var document in documents)
        {
            var captured = await repository.CaptureAsync(new CaptureRequest(document.Content, document.Title), actor);
            identities.Add(captured.Document.Id, document.Id);
        }
        foreach (var work in await embeddings.PendingAsync(space.Id, documents.Length))
        {
            var chunk = Assert.Single(work.Chunks);
            var record = vectors.GetProperty(identities[work.DocumentId]);
            Assert.Equal(record.GetProperty("input_sha256").GetString(), Hash(Encoding.UTF8.GetBytes(chunk.Context + "\n" + chunk.Text)));
            Assert.True(await embeddings.SaveAsync(space, work, new Dictionary<string, float[]> { [chunk.Id] = Decode(record, dimensions) }));
        }
        Assert.True(await embeddings.ActivateAsync(space.Id));
        var retrieval = new SqliteRetrievalRepository(stores.Index, new UnusedProtection());
        foreach (var mode in new[] { "keyword", "semantic", "hybrid" })
        {
            double recall5 = 0, recall8 = 0, reciprocalRank = 0;
            foreach (var question in questions)
            {
                var record = vectors.GetProperty(question.Id);
                Assert.Equal(record.GetProperty("input_sha256").GetString(), Hash(Encoding.UTF8.GetBytes(question.Query)));
                var result = await retrieval.SearchAsync(new SearchRequest(question.Query, mode, Limit: 8), Decode(record, dimensions), space.Id);
                Assert.Empty(result.Degraded);
                var ranked = result.Results.Select(hit => identities[hit.DocumentId]).Distinct().ToArray();
                recall5 += question.Expected.Count(id => ranked.Take(5).Contains(id)) / (double)question.Expected.Length;
                recall8 += question.Expected.Count(id => ranked.Take(8).Contains(id)) / (double)question.Expected.Length;
                var rank = Array.FindIndex(ranked, id => question.Expected.Contains(id));
                if (rank >= 0) reciprocalRank += 1d / (rank + 1);
            }
            recall5 /= questions.Length; recall8 /= questions.Length; reciprocalRank /= questions.Length;
            output.WriteLine($"{mode}: recall@5={recall5:F4}, recall@8={recall8:F4}, MRR@8={reciprocalRank:F4}; documents={documents.Length}, questions={questions.Length}");
            if (mode == "hybrid") Assert.True(recall8 >= .85, $"Hybrid seed recall@8 was {recall8:F4}; minimum is 0.85.");
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static float[] Decode(JsonElement item, int dimensions)
    {
        var bytes = Convert.FromBase64String(item.GetProperty("float32_le_base64").GetString()!);
        Assert.Equal(dimensions * sizeof(float), bytes.Length);
        var vector = new float[dimensions];
        for (var i = 0; i < dimensions; i++) vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)));
        return vector;
    }
    private sealed record SeedDocument(string Id, string Title, string Content);
    private sealed record SeedQuestion(string Id, string Query, string[] Expected);
    private sealed class UnusedProtection : IPayloadProtection
    {
        public string Protect(string purpose, string plaintext) => throw new NotSupportedException();
        public string Unprotect(string purpose, string protectedText) => throw new NotSupportedException();
    }
}
