using System.Diagnostics;
using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Storage.Retrieval;
using Xunit;
using Xunit.Abstractions;

namespace SecondBrain.Storage.Tests;

/// <summary>Explicit, resource-intensive diagnostics; run separately from deterministic CI.</summary>
public sealed class RetrievalScaleQualification(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Qualification")]
    public Task SyntheticMillionChunkRetrievalReportsWarmLatency() => RunAsync(false);

    [Fact]
    [Trait("Category", "Qualification")]
    public Task RecordedSeedMillionChunkRetrievalReportsWarmLatency() => RunAsync(true);

    private async Task RunAsync(bool representative)
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        var evaluation = Path.Combine(AppContext.BaseDirectory, "Evaluation");
        var documentBytes = representative ? await File.ReadAllBytesAsync(Path.Combine(evaluation, "documents.jsonl")) : [];
        var questionBytes = representative ? await File.ReadAllBytesAsync(Path.Combine(evaluation, "questions.jsonl")) : [];
        using var recording = representative ? JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(evaluation, "embeddings.json"))) : null;
        var documents = representative ? Encoding.UTF8.GetString(documentBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<SeedDocument>(line, Json)!).ToArray() : [];
        var questions = representative ? Encoding.UTF8.GetString(questionBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<SeedQuestion>(line, Json)!).ToArray() : [new SeedQuestion("broad", "quasar", [])];
        if (representative)
        {
            Assert.Equal(recording!.RootElement.GetProperty("documents_sha256").GetString(), Hash(documentBytes));
            Assert.Equal(recording.RootElement.GetProperty("questions_sha256").GetString(), Hash(questionBytes));
            Assert.Equal(1024, recording.RootElement.GetProperty("descriptor").GetProperty("dimensions").GetInt32());
        }
        var paletteSize = representative ? documents.Length : 256;
        await stores.Index.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var create = connection.CreateCommand(); create.Transaction = transaction;
            create.CommandText = "CREATE TABLE qualification_vectors(id INTEGER PRIMARY KEY,vector BLOB NOT NULL,title TEXT NOT NULL,context TEXT NOT NULL,text TEXT NOT NULL,search_text TEXT NOT NULL,offsets BLOB NOT NULL,input_hash TEXT NOT NULL,token_count INTEGER NOT NULL)";
            await create.ExecuteNonQueryAsync(token);
            var random = new Random(20261009);
            for (var i = 0; i < paletteSize; i++)
            {
                var document = representative ? documents[i] : new SeedDocument("broad", "Scale fixture", "quasar observation");
                var projection = TextProcessor.Process("fixture", document.Title, document.Content);
                var chunk = Assert.Single(projection.Chunks);
                byte[] bytes;
                if (representative)
                {
                    var record = recording!.RootElement.GetProperty("vectors").GetProperty(document.Id);
                    Assert.Equal(record.GetProperty("input_sha256").GetString(), chunk.InputHash);
                    bytes = Convert.FromBase64String(record.GetProperty("float32_le_base64").GetString()!);
                    Assert.Equal(1024 * sizeof(float), bytes.Length);
                }
                else
                {
                    var values = Enumerable.Range(0, 1024).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
                    var magnitude = Math.Sqrt(values.Sum(value => (double)value * value));
                    for (var j = 0; j < values.Length; j++) values[j] = (float)(values[j] / magnitude);
                    bytes = new byte[values.Length * sizeof(float)]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
                }
                await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO qualification_vectors VALUES($id,$vector,$title,$context,$text,$search,$offsets,$hash,$tokens)";
                foreach (var (name, value) in new (string, object)[] { ("$id", i), ("$vector", bytes), ("$title", document.Title), ("$context", chunk.Context), ("$text", chunk.Text), ("$search", projection.SearchText), ("$offsets", JsonSerializer.SerializeToUtf8Bytes(projection.OffsetMap)), ("$hash", chunk.InputHash), ("$tokens", chunk.TokenCount) })
                { var parameter = insert.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; insert.Parameters.Add(parameter); }
                await insert.ExecuteNonQueryAsync(token);
            }
            return true;
        });
        var seed = Stopwatch.StartNew();
        for (var first = 0; first < 100000; first += 1000)
        await stores.Index.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                WITH RECURSIVE n(x) AS(VALUES($first) UNION ALL SELECT x+1 FROM n WHERE x<$first+999)
                INSERT INTO indexed_documents(document_id,revision,type,title,props_json,status,updated_at)
                SELECT printf('doc-%06d',x),1,CASE WHEN x<10000 THEN 'article' ELSE 'note' END,v.title,'{}','indexed','2026-10-09T00:00:00.0000000Z' FROM n JOIN qualification_vectors v ON v.id=x%$palette;
                INSERT INTO indexed_document_generations(document_id,generation_json)
                SELECT document_id,'{"source_generation":1,"extraction_generation":1,"normalization_generation":1,"chunk_generation":1}' FROM indexed_documents WHERE document_id>=printf('doc-%06d',$first) AND document_id<printf('doc-%06d',$first+1000);
                INSERT INTO indexed_document_sources(document_id,source_id,path,mime)
                SELECT document_id,'qualification',document_id||'.md','text/markdown' FROM indexed_documents WHERE document_id>=printf('doc-%06d',$first) AND document_id<printf('doc-%06d',$first+1000);
                INSERT INTO document_text(document_id,revision,generation_json,lossless_text,search_text,offset_map,indexed_at)
                SELECT d.document_id,1,g.generation_json,v.text,v.search_text,v.offsets,d.updated_at FROM indexed_documents d JOIN indexed_document_generations g ON g.document_id=d.document_id JOIN qualification_vectors v ON v.id=CAST(substr(d.document_id,5) AS INTEGER)%$palette WHERE d.document_id>=printf('doc-%06d',$first) AND d.document_id<printf('doc-%06d',$first+1000);
                WITH RECURSIVE n(x) AS(VALUES($first*10) UNION ALL SELECT x+1 FROM n WHERE x<($first+1000)*10-1)
                INSERT INTO chunks(rowid,id,document_id,revision,ordinal,context,text,start_offset,end_offset,token_count,input_hash)
                SELECT x+1,printf('chunk-%07d',x),printf('doc-%06d',x/10),1,x%10,v.context,v.text,0,length(v.text),v.token_count,v.input_hash FROM n JOIN qualification_vectors v ON v.id=(x/10)%$palette;
                INSERT INTO chunks_fts_source SELECT c.rowid,v.search_text,c.context,v.title FROM chunks c JOIN qualification_vectors v ON v.id=((c.rowid-1)/10)%$palette WHERE c.rowid>$first*10 AND c.rowid<=($first+1000)*10;
                INSERT INTO chunks_fts(rowid,text,context,title) SELECT rowid,text,context,title FROM chunks_fts_source WHERE rowid>$first*10 AND rowid<=($first+1000)*10;
                INSERT INTO chunk_vectors(space_id,chunk_rowid,vector) SELECT 'scale',c.rowid,v.vector FROM chunks c JOIN qualification_vectors v ON v.id=((c.rowid-1)/10)%$palette WHERE c.rowid>$first*10 AND c.rowid<=($first+1000)*10;
                """;
            var palette = command.CreateParameter(); palette.ParameterName = "$palette"; palette.Value = paletteSize; command.Parameters.Add(palette);
            var start = command.CreateParameter(); start.ParameterName = "$first"; start.Value = first; command.Parameters.Add(start);
            await command.ExecuteNonQueryAsync(token); return true;
        });
        output.WriteLine($"seed_seconds={seed.Elapsed.TotalSeconds:F3}; 100000 documents; 1000000 chunks; 1024 dimensions; profile={(representative ? "recorded_seed" : "broad_allterm")}; palette={paletteSize}; projected generations/text/offsets/sources; temporary filesystem={root.Path}");
        var repository = new SqliteRetrievalRepository(stores.Index, new UnusedProtection());
        var queryVectors = questions.Select(question =>
        {
            if (!representative) return Enumerable.Range(0, 1024).Select(i => (float)Math.Sin(i + .5)).ToArray();
            var record = recording!.RootElement.GetProperty("vectors").GetProperty(question.Id);
            Assert.Equal(record.GetProperty("input_sha256").GetString(), Hash(Encoding.UTF8.GetBytes(question.Query)));
            var bytes = Convert.FromBase64String(record.GetProperty("float32_le_base64").GetString()!);
            Assert.Equal(1024 * sizeof(float), bytes.Length);
            return Enumerable.Range(0, 1024).Select(i => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)))).ToArray();
        }).ToArray();
        var cold = Stopwatch.StartNew();
        await repository.SearchAsync(new(questions[0].Query, "hybrid"), queryVectors[0], "scale");
        output.WriteLine($"cold_load_and_search_ms={cold.Elapsed.TotalMilliseconds:F3}");
        foreach (var mode in new[] { "keyword", "semantic", "hybrid" })
        foreach (var filtered in new[] { true, false })
        {
            var times = new List<double>();
            var recall = 0d;
            for (var i = 0; i < 20; i++)
            {
                var timer = Stopwatch.StartNew();
                var questionIndex = i % questions.Length;
                var question = questions[questionIndex];
                var result = await repository.SearchAsync(new(question.Query, mode, filtered ? new RetrievalFilters(Type: "article") : null), queryVectors[questionIndex], "scale");
                times.Add(timer.Elapsed.TotalMilliseconds);
                Assert.Equal(8, result.Results.Count); Assert.Empty(result.Degraded);
                if (representative)
                {
                    var topics = result.Results.Select(hit => documents[int.Parse(hit.DocumentId.AsSpan(4), CultureInfo.InvariantCulture) % paletteSize].Id).ToHashSet(StringComparer.Ordinal);
                    recall += question.Expected.Count(topics.Contains) / (double)question.Expected.Length;
                }
            }
            times.Sort(); output.WriteLine($"{mode}_{(filtered ? "100k_filtered" : "1m_unfiltered")}_p95_ms={times[18].ToString("F3", CultureInfo.InvariantCulture)}; provider_ms=0; scaled_topic_recall@8={(representative ? (recall / 20).ToString("F4", CultureInfo.InvariantCulture) : "not_applicable")}");
        }
        var structured = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var timer = Stopwatch.StartNew();
            var page = await repository.QueryAsync(new(Type: "article"), new("qualification", 1, 1, "fixture", "api_key"));
            structured.Add(timer.Elapsed.TotalMilliseconds); Assert.Equal(50, page.Records.Count);
        }
        structured.Sort(); output.WriteLine($"structured_p95_ms={structured[18]:F3}; managed_bytes={GC.GetTotalMemory(false)}; working_set={Process.GetCurrentProcess().WorkingSet64}");
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed record SeedDocument(string Id, string Title, string Content);
    private sealed record SeedQuestion(string Id, string Query, string[] Expected);

    private sealed class UnusedProtection : IPayloadProtection
    {
        public string Protect(string purpose, string plaintext) => plaintext;
        public string Unprotect(string purpose, string protectedText) => protectedText;
    }
}
