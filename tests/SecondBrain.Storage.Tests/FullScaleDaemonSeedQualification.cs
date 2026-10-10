using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using Xunit;
using Xunit.Abstractions;

namespace SecondBrain.Storage.Tests;

/// <summary>Opt-in persistent fixture for the real, isolated deployment harness. Never targets the default data root.</summary>
public sealed class FullScaleDaemonSeedQualification(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Qualification")]
    [Trait("Category", "FullScaleFixture")]
    public async Task SeedAuthoritativeManagedMillionChunkFixture()
    {
        var root = Environment.GetEnvironmentVariable("SECONDBRAIN_FULLSCALE_ROOT");
        if (root is null) return;
        root = Path.GetFullPath(root);
        Assert.Contains("m1-qualification", root, StringComparison.Ordinal);
        var count = int.Parse(Environment.GetEnvironmentVariable("SECONDBRAIN_FULLSCALE_DOCUMENTS") ?? "100000");
        var containerRoot = Environment.GetEnvironmentVariable("SECONDBRAIN_FULLSCALE_CONTAINER_ROOT") ?? "/srv/secondbrain";
        var evaluation = Path.Combine(AppContext.BaseDirectory, "Evaluation");
        var documentBytes = await File.ReadAllBytesAsync(Path.Combine(evaluation, "documents.jsonl"));
        var questionBytes = await File.ReadAllBytesAsync(Path.Combine(evaluation, "questions.jsonl"));
        using var recording = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(evaluation, "embeddings.json")));
        Assert.Equal(recording.RootElement.GetProperty("documents_sha256").GetString(), Hash(documentBytes));
        Assert.Equal(recording.RootElement.GetProperty("questions_sha256").GetString(), Hash(questionBytes));
        Assert.Equal(1024, recording.RootElement.GetProperty("descriptor").GetProperty("dimensions").GetInt32());
        var docs = Encoding.UTF8.GetString(documentBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<SeedDocument>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
        await using var stores = new MigrationTestStores(root);
        await stores.Runner.MigrateAsync();
        var coordinator = new PublicationCoordinator(stores.State, stores.Index);
        var embeddings = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var space = await embeddings.ActiveAsync();
        if (space is null)
        {
            var descriptor = recording.RootElement.GetProperty("descriptor");
            var endpoint = Environment.GetEnvironmentVariable("SECONDBRAIN_FULLSCALE_PROVIDER_ENDPOINT")
                ?? throw new InvalidOperationException("Set the exact deployed embed endpoint before creating an embedding space.");
            var provider = descriptor.GetProperty("provider").GetString()!;
            var model = descriptor.GetProperty("model").GetString()!;
            space = await embeddings.EnsureSpaceAsync(EmbeddingFingerprint.Create(provider, endpoint, model, 1024), provider, model, 1024);
        }
        Assert.Equal(1024, space.Dimensions);
        Assert.Equal(recording.RootElement.GetProperty("descriptor").GetProperty("model").GetString(), space.Model);
        await MigrationTestSql.WriteAsync(stores.State, "INSERT OR IGNORE INTO sources(id,kind,name,created_at) VALUES('fullscale-qualification','api','Full scale qualification','2026-10-09T00:00:00Z')");
        var vault = Path.Combine(root, "vault", "originals");
        Directory.CreateDirectory(vault);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(vault, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var palette = docs.Select(doc =>
        {
            var unit = Assert.Single(TextProcessor.Process("unit", doc.Title, doc.Content).Chunks);
            var record = recording.RootElement.GetProperty("vectors").GetProperty(doc.Id);
            Assert.Equal(record.GetProperty("input_sha256").GetString(), unit.InputHash);
            var bytes = Convert.FromBase64String(record.GetProperty("float32_le_base64").GetString()!);
            Assert.Equal(4096, bytes.Length);
            return (Doc: doc, Unit: unit, Vector: bytes, Body: string.Concat(Enumerable.Repeat(doc.Content, 10)));
        }).ToArray();
        // Cache exactly the recorded embedding inputs; owners are added for every actual document below.
        foreach (var item in palette)
            await stores.Index.QueueWriteAsync(async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT OR IGNORE INTO embedding_cache(space_id,input_hash,vector,created_at) VALUES($space,$hash,$vector,'2026-10-09T00:00:00Z')";
                foreach (var (name, value) in new (string, object)[] { ("$space", space.Id), ("$hash", item.Unit.InputHash), ("$vector", item.Vector) })
                { var p = command.CreateParameter(); p.ParameterName = name; p.Value = value; command.Parameters.Add(p); }
                return await command.ExecuteNonQueryAsync(token);
            });
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var id = DocumentId(i);
            if (await MigrationTestSql.ScalarAsync(stores.State, $"SELECT 1 FROM documents WHERE id='{id}'") is not null) continue;
            var item = palette[i % palette.Length];
            var bytes = Encoding.UTF8.GetBytes(item.Body);
            // Production normalization and chunker, with no overlap, produce ten exact recorded inputs.
            var projection = TextProcessor.Process(id, item.Doc.Title, item.Body, path: Path.Combine(containerRoot, "vault", "originals", id + ".md"),
                targetTokens: Encoding.UTF8.GetByteCount(item.Doc.Content), overlapTokens: 0);
            Assert.Equal(10, projection.Chunks.Count);
            Assert.All(projection.Chunks, chunk => { Assert.Equal(item.Unit.InputHash, chunk.InputHash); Assert.Equal(chunk.Text, projection.LosslessText[chunk.StartOffset..chunk.EndOffset]); });
            var file = Path.Combine(vault, id + ".md");
            await using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await stream.WriteAsync(bytes); stream.Flush(flushToDisk: true); }
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            SecondBrain.Infrastructure.Security.UnixSecurity.SyncDirectory(vault);
            var result = await coordinator.PublishAsync(new DocumentPublication(id, "fullscale-qualification", 0,
                "{\"chunker\":1,\"extractor\":1,\"normalization\":1}", "qualification-seed", item.Doc.Title, Hash(bytes), "qualification:recorded-qwen")
            { Projection = projection, Type = i < count / 10 ? "article" : "note", Mime = "text/markdown", SizeBytes = bytes.Length });
            Assert.Equal(PublicationOutcome.Published, result);
            if ((i + 1) % 1000 == 0) { await File.WriteAllTextAsync(Path.Combine(root, "fullscale-progress.json"), JsonSerializer.Serialize(new { published = i + 1, elapsed_seconds = timer.Elapsed.TotalSeconds })); output.WriteLine($"published={i + 1}; elapsed_seconds={timer.Elapsed.TotalSeconds:F3}"); Console.WriteLine($"fullscale published={i + 1}; elapsed_seconds={timer.Elapsed.TotalSeconds:F3}"); }
        }
        // Bounded production writer transactions: populate live vectors from committed chunks and recorded cache.
        for (var first = 0; first < count; first += 1000)
            await stores.Index.QueueWriteAsync(async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR IGNORE INTO embedding_cache_owners(input_hash,document_id)
                      SELECT DISTINCT input_hash,document_id FROM chunks WHERE document_id >= $first AND document_id < $last;
                    INSERT OR IGNORE INTO chunk_vectors(space_id,chunk_rowid,vector)
                      SELECT e.space_id,c.rowid,e.vector FROM chunks c JOIN embedding_cache e ON e.input_hash=c.input_hash AND e.space_id=$space
                      WHERE c.document_id >= $first AND c.document_id < $last;
                    """;
                foreach (var (name, value) in new[] { ("$first", DocumentId(first)), ("$last", DocumentId(Math.Min(first + 1000, count))), ("$space", space.Id) })
                { var p = command.CreateParameter(); p.ParameterName = name; p.Value = value; command.Parameters.Add(p); }
                return await command.ExecuteNonQueryAsync(token);
            });
        Assert.True(await embeddings.ActivateAsync(space.Id));
        Assert.Equal((long)count, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM documents WHERE source_id='fullscale-qualification' AND publish_state='published'"));
        Assert.Equal((long)count, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM document_projections p JOIN documents d ON d.id=p.document_id WHERE d.source_id='fullscale-qualification'"));
        Assert.Equal((long)count * 10, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM chunks WHERE document_id >= '01K00000000000000000000000' AND document_id < '01K00000000000000000ZZZZZZ'"));
        Assert.Equal((long)count * 10, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM chunk_vectors v JOIN chunks c ON c.rowid=v.chunk_rowid WHERE c.document_id >= '01K00000000000000000000000' AND c.document_id < '01K00000000000000000ZZZZZZ'"));
        var report = new { profile = "authoritative-managed-recorded-qwen", documents = count, chunks = count * 10, dimensions = 1024,
            palette = palette.Length, provider_calls = 0, space_id = space.Id, seed_seconds = timer.Elapsed.TotalSeconds,
            documents_sha256 = Hash(documentBytes), questions_sha256 = Hash(questionBytes), chunking = "production TextProcessor, target=unit UTF8 bytes, overlap=0",
            publication = "production PublicationCoordinator; durable files/revisions/fences/payloads/payload_refs/projections; bounded vector/cache ownership commits" };
        await File.WriteAllTextAsync(Path.Combine(root, "fullscale-seed.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine(JsonSerializer.Serialize(report));
    }
    private static string DocumentId(int number)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        Span<char> suffix = stackalloc char[6];
        for (var i = 5; i >= 0; i--) { suffix[i] = alphabet[number & 31]; number >>= 5; }
        return "01K00000000000000000" + new string(suffix);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed record SeedDocument(string Id, string Title, string Content);
}
