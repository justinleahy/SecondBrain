using System.Text.Json;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using SecondBrain.Storage.Retrieval;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class M1PublicationRetrievalTests
{
    private static readonly IngestionAuthority Key = new("capture-key", 1, 1, "capture fixture", "api_key");
    private static readonly IngestionAuthority Session = new("capture-session", 1, 1, "session fixture", "session");

    [Theory]
    [InlineData(CrashPointNames.AfterStatePublishing)]
    [InlineData(CrashPointNames.AfterIndexCommit)]
    public async Task InterruptedCaptureRecoversAdmissionAndReplaysWithoutDuplicates(string point)
    {
        using var root = new MigrationTestDataRoot();
        string id;
        var request = new CaptureRequest("durable astronomy observation", "Observatory", Tags: ["science"]);
        await using (var stores = new MigrationTestStores(root.Path))
        {
            await SeedAsync(stores);
            var documents = Documents(stores, new CrashAt(point));
            await Assert.ThrowsAsync<InjectedFailure>(() => documents.CaptureAsync(request, Key, "retry-capture").AsTask());
            id = (string)(await MigrationTestSql.ScalarAsync(stores.State, "SELECT id FROM documents"))!;
            Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM jobs"));
            Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM idempotency"));
        }
        for (var restart = 0; restart < 2; restart++)
        {
            await using var stores = new MigrationTestStores(root.Path);
            await stores.Runner.MigrateAsync();
            await new PublicationCoordinator(stores.State, stores.Index).RecoverAsync();
            var result = await Documents(stores).CaptureAsync(request, Key, "retry-capture");
            Assert.Equal(id, result.Document.Id);
            Assert.Equal(1, result.Document.Revision);
            Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM documents"));
            Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM jobs"));
            var found = await Retrieval(stores).SearchAsync(new SearchRequest("astronomy", "keyword"));
            Assert.Equal(id, Assert.Single(found.Results).DocumentId);
        }
    }

    [Theory]
    [InlineData(CrashPointNames.AfterStatePublishing, "original", "replacement")]
    [InlineData(CrashPointNames.AfterIndexCommit, "replacement", "original")]
    public async Task InterruptedReplacementExposesOneCompleteProjection(string point, string visible, string absent)
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        var captured = await Documents(stores).CaptureAsync(new CaptureRequest("original signal", "Old title", Tags: ["old"]), Key);
        var crashing = Documents(stores, new CrashAt(point));
        await Assert.ThrowsAsync<InjectedFailure>(() => crashing.MutateAsync(captured.Document.Id, 1, "update",
            JsonSerializer.SerializeToElement(new { content = "replacement signal", title = "New title" }), Session).AsTask());
        var retrieval = Retrieval(stores);
        var hit = Assert.Single((await retrieval.SearchAsync(new SearchRequest(visible, "keyword"))).Results);
        Assert.Equal(visible == "original" ? "Old title" : "New title", hit.Title);
        Assert.Equal(visible == "original" ? 1 : 2, hit.Revision);
        Assert.Empty((await retrieval.SearchAsync(new SearchRequest(absent, "keyword"))).Results);
        await new PublicationCoordinator(stores.State, stores.Index).RecoverAsync();
        Assert.Equal(2, Assert.Single((await retrieval.SearchAsync(new SearchRequest("replacement", "keyword"))).Results).Revision);
        Assert.Empty((await retrieval.SearchAsync(new SearchRequest("original", "keyword"))).Results);
    }

    [Fact]
    public async Task FiltersApplyBeforeBothLegsWithMoreThanFiftyDistractors()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        var documents = Documents(stores);
        for (var i = 0; i < 60; i++)
            await documents.CaptureAsync(new CaptureRequest("quasar", "quasar", Tags: ["distractor"]), Key);
        var target = await documents.CaptureAsync(new CaptureRequest("a long account of observations of a distant quasar and its surrounding interstellar dust", "Target", Tags: ["wanted"]), Key);
        var embeddings = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var space = await embeddings.EnsureSpaceAsync("fixture-fingerprint", "fixture", "recorded-filter-case", 2);
        foreach (var work in await embeddings.PendingAsync(space.Id, 100))
            Assert.True(await embeddings.SaveAsync(space, work, work.Chunks.ToDictionary(chunk => chunk.Id,
                _ => work.DocumentId == target.Document.Id ? new float[] { .6f, .8f } : [1, 0])));
        var retrieval = Retrieval(stores);
        foreach (var mode in new[] { "keyword", "semantic", "hybrid" })
        {
            var result = await retrieval.SearchAsync(new SearchRequest("quasar", mode, new RetrievalFilters(Tags: ["wanted"])), [1, 0], space.Id);
            Assert.Equal(target.Document.Id, Assert.Single(result.Results).DocumentId);
            Assert.Empty(result.Degraded);
        }
    }

    [Fact]
    public async Task KeyDeleteCreatesDurableProposalWithoutChangingDocument()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        var documents = Documents(stores);
        var captured = await documents.CaptureAsync(new CaptureRequest("keep this content", "Keep"), Key);
        var proposal = await documents.MutateAsync(captured.Document.Id, 1, "delete", JsonSerializer.SerializeToElement(new { }), Key);
        Assert.Equal("proposed", proposal.Document.Status);
        var unchanged = await documents.GetAsync(captured.Document.Id, true);
        Assert.NotNull(unchanged);
        Assert.Equal(1, unchanged.Revision);
        Assert.Equal("keep this content", unchanged.Text);
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM pending_operations WHERE status='pending'"));
        Assert.Single((await Retrieval(stores).SearchAsync(new SearchRequest("content", "keyword"))).Results);
    }

    [Fact]
    public async Task LexicalPrefixGrowsPastDominantDocumentWithoutChangingTiesOrDiversity()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.Index, """
            WITH RECURSIVE n(x) AS(VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<40)
            INSERT INTO indexed_documents(document_id,revision,type,title,props_json,status,updated_at)
            SELECT printf('doc-%03d',x),1,'note','Fixture','{}','indexed','2026-10-09T00:00:00Z' FROM n;
            WITH RECURSIVE n(x) AS(VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<619)
            INSERT INTO chunks(rowid,id,document_id,revision,ordinal,context,text,start_offset,end_offset,token_count,input_hash)
            SELECT x+1,printf('chunk-%04d',x),printf('doc-%03d',CASE WHEN x<500 THEN 0 ELSE (x-500)/3+1 END),1,
              CASE WHEN x<500 THEN x ELSE (x-500)%3 END,'Fixture','quasar',0,6,8,printf('hash-%04d',x) FROM n;
            INSERT INTO chunks_fts_source SELECT rowid,text,context,'Fixture' FROM chunks;
            INSERT INTO chunks_fts(rowid,text,context,title) SELECT rowid,text,context,title FROM chunks_fts_source;
            """);
        var response = await Retrieval(stores).SearchAsync(new SearchRequest("quasar", "keyword", Limit:50));
        Assert.Equal(50, response.Results.Count);
        Assert.All(response.Results.GroupBy(hit => hit.DocumentId), group => Assert.InRange(group.Count(), 1, 3));
        Assert.Equal(new[] { "chunk-0000", "chunk-0001", "chunk-0002", "chunk-0500" }, response.Results.Take(4).Select(hit => hit.ChunkId));
        Assert.Equal(response.Results.Select(hit => hit.ChunkId).Order(StringComparer.Ordinal), response.Results.Select(hit => hit.ChunkId));
    }

    [Fact]
    public async Task CommittedReplacementInvalidatesPreviouslyLoadedVectorGeneration()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        var capture = await Documents(stores).CaptureAsync(new CaptureRequest("old observation", "Old"), Key);
        var embeddings = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var space = await embeddings.EnsureSpaceAsync("coherence-fixture", "fixture", "coherence-model", 2);
        var original = Assert.Single(await embeddings.PendingAsync(space.Id));
        Assert.True(await embeddings.SaveAsync(space, original, original.Chunks.ToDictionary(chunk => chunk.Id, _ => new float[] { 1, 0 })));
        var retrieval = Retrieval(stores);
        Assert.Equal(1, Assert.Single((await retrieval.SearchAsync(new SearchRequest("observation", "semantic"), [1, 0], space.Id)).Results).Revision);
        await Assert.ThrowsAsync<InjectedFailure>(() => Documents(stores, new CrashAt(CrashPointNames.AfterIndexCommit)).MutateAsync(capture.Document.Id, 1,
            "update", JsonSerializer.SerializeToElement(new { content = "new observation", title = "New" }), Session).AsTask());
        var replaced = await retrieval.SearchAsync(new SearchRequest("observation", "semantic"), [1, 0], space.Id);
        Assert.Empty(replaced.Results);
        Assert.Contains("vector-pending", replaced.Degraded);
        await new PublicationCoordinator(stores.State, stores.Index).RecoverAsync();
        var pending = Assert.Single(await embeddings.PendingAsync(space.Id));
        Assert.True(await embeddings.SaveAsync(space, pending, pending.Chunks.ToDictionary(chunk => chunk.Id, _ => new float[] { 0, 1 })));
        var fresh = Assert.Single((await retrieval.SearchAsync(new SearchRequest("observation", "semantic"), [0, 1], space.Id)).Results);
        Assert.Equal(2, fresh.Revision);
        Assert.Equal("New", fresh.Title);
    }

    [Fact]
    public async Task RetiredFingerprintCanBeSelectedAgainAndRepeatedActivationDoesNotReloadVectors()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        await Documents(stores).CaptureAsync(new CaptureRequest("observation", "Space cycle"), Key);
        var embeddings = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var first = await embeddings.EnsureSpaceAsync("cycle-a", "fixture", "a", 2);
        var second = await embeddings.EnsureSpaceAsync("cycle-b", "fixture", "b", 2);
        foreach (var space in new[] { first, second })
        {
            var work = Assert.Single(await embeddings.PendingAsync(space.Id));
            Assert.True(await embeddings.SaveAsync(space, work, work.Chunks.ToDictionary(chunk => chunk.Id, _ => new float[] { 1, 0 })));
            Assert.True(await embeddings.ActivateAsync(space.Id));
        }
        var revived = await embeddings.EnsureSpaceAsync("cycle-a", "fixture", "a", 2);
        Assert.Equal(first.Id, revived.Id);
        Assert.Equal("building", revived.Status);
        Assert.True(await embeddings.ActivateAsync(revived.Id));
        Assert.Equal(first.Id, (await embeddings.ActiveAsync())!.Id);
        var retrieval = Retrieval(stores);
        await retrieval.SearchAsync(new SearchRequest("observation", "semantic"), [1, 0], first.Id);
        var registry = VectorSnapshotRegistry.For(stores.Index);
        var rowsRead = registry.VectorRowsRead;
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await embeddings.ActivateAsync(first.Id));
            await retrieval.SearchAsync(new SearchRequest("observation", "semantic"), [1, 0], first.Id);
        }
        Assert.Equal(rowsRead, registry.VectorRowsRead);
    }

    [Fact]
    public async Task EmbeddingPublicationReadsOnlyChangedDocumentAndRemovalReadsNoUnrelatedVectors()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        var documents = Documents(stores);
        var first = await documents.CaptureAsync(new CaptureRequest("first observation", "First"), Key);
        await documents.CaptureAsync(new CaptureRequest("second observation", "Second"), Key);
        var embeddings = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var space = await embeddings.EnsureSpaceAsync("delta", "fixture", "delta", 2);
        foreach (var work in await embeddings.PendingAsync(space.Id))
            Assert.True(await embeddings.SaveAsync(space, work, work.Chunks.ToDictionary(chunk => chunk.Id, _ => new float[] { 1, 0 })));
        var retrieval = Retrieval(stores);
        Assert.Equal(2, (await retrieval.SearchAsync(new SearchRequest("observation", "semantic"), [1, 0], space.Id)).Results.Count);
        var registry = VectorSnapshotRegistry.For(stores.Index);
        var initialReads = registry.VectorRowsRead;
        await documents.MutateAsync(first.Document.Id, 1, "update", JsonSerializer.SerializeToElement(new { content = "changed observation" }), Session);
        var pending = Assert.Single(await embeddings.PendingAsync(space.Id));
        Assert.True(await embeddings.SaveAsync(space, pending, pending.Chunks.ToDictionary(chunk => chunk.Id, _ => new float[] { 0, 1 })));
        Assert.Equal(2, (await retrieval.SearchAsync(new SearchRequest("observation", "semantic"), [0, 1], space.Id)).Results.Count);
        Assert.Equal(initialReads + pending.Chunks.Count, registry.VectorRowsRead);
        var afterReplacement = registry.VectorRowsRead;
        await documents.MutateAsync(first.Document.Id, 2, "delete", JsonSerializer.SerializeToElement(new { }), Session);
        Assert.Single((await retrieval.SearchAsync(new SearchRequest("observation", "semantic"), [0, 1], space.Id)).Results);
        Assert.Equal(afterReplacement, registry.VectorRowsRead);
    }

    [Fact]
    public async Task LateMismatchedChunkRollsBackTheWholeVectorBatchAndCache()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        await Documents(stores).CaptureAsync(new CaptureRequest(new string('a', 1200), "Long"), Key);
        var embeddings = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var space = await embeddings.EnsureSpaceAsync("rollback-fixture", "fixture", "rollback-model", 2);
        var work = Assert.Single(await embeddings.PendingAsync(space.Id));
        Assert.True(work.Chunks.Count > 1);
        var chunks = work.Chunks.ToArray();
        chunks[^1] = chunks[^1] with { InputHash = "obsolete-input-hash" };
        Assert.False(await embeddings.SaveAsync(space, work with { Chunks = chunks }, chunks.ToDictionary(chunk => chunk.Id, _ => new float[] { 1, 0 })));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM chunk_vectors"));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM embedding_cache"));
    }

    [Fact]
    public async Task QueryCursorRejectsChangedQueryAndCredentialGeneration()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        for (var i = 0; i < 3; i++) await Documents(stores).CaptureAsync(new CaptureRequest("text", "Same title"), Key);
        var retrieval = Retrieval(stores);
        var request = new QueryRequest(Sort: "title", Direction: "asc", Limit: 1);
        var first = await retrieval.QueryAsync(request, Key);
        Assert.NotNull(first.NextCursor);
        var second = await retrieval.QueryAsync(request with { Cursor = first.NextCursor }, Key);
        Assert.NotEqual(Assert.Single(first.Records).Id, Assert.Single(second.Records).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => retrieval.QueryAsync(request with { Cursor = first.NextCursor, Type = "article" }, Key).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => retrieval.QueryAsync(request with { Cursor = first.NextCursor }, Key with { CredentialGeneration = 2 }).AsTask());
    }

    [Fact]
    public async Task PhysicalIndexLossRebuildsTextChunksAndSuppressionsWithoutNewRevisions()
    {
        using var root = new MigrationTestDataRoot();
        string id;
        string authored;
        string projection;
        string suppression;
        await using (var stores = new MigrationTestStores(root.Path))
        {
            await SeedAsync(stores);
            var documents = Documents(stores);
            var capture = await documents.CaptureAsync(new CaptureRequest("# Astronomy\n\nCafé observations of a quasar.\n", "Notebook", Tags: ["science"]), Key);
            id = capture.Document.Id;
            var removed = await documents.CaptureAsync(new CaptureRequest("suppressed private phrase", "Suppressed"), Key);
            await documents.MutateAsync(removed.Document.Id, 1, "delete", JsonSerializer.SerializeToElement(new { }), Session);
            authored = JsonSerializer.Serialize(await documents.GetAsync(id, true));
            projection = (string)(await MigrationTestSql.ScalarAsync(stores.Index,
                "SELECT json_group_array(json_object('id',id,'revision',revision,'ordinal',ordinal,'text',text,'context',context,'start',start_offset,'end',end_offset,'hash',input_hash)) FROM (SELECT * FROM chunks ORDER BY document_id,ordinal)"))!;
            suppression = (string)(await MigrationTestSql.ScalarAsync(stores.State,
                "SELECT json_group_array(json_object('scope',scope,'key',key,'reason',reason)) FROM suppressions"))!;
        }
        Directory.Delete(Path.Combine(root.Path, "index"), recursive: true);
        for (var restart = 0; restart < 2; restart++)
        {
            await using var stores = new MigrationTestStores(root.Path);
            await stores.Runner.MigrateAsync();
            var documents = Documents(stores);
            await documents.RebuildAsync();
            Assert.Equal(authored, JsonSerializer.Serialize(await documents.GetAsync(id, true)));
            Assert.Equal(projection, await MigrationTestSql.ScalarAsync(stores.Index,
                "SELECT json_group_array(json_object('id',id,'revision',revision,'ordinal',ordinal,'text',text,'context',context,'start',start_offset,'end',end_offset,'hash',input_hash)) FROM (SELECT * FROM chunks ORDER BY document_id,ordinal)"));
            Assert.Equal(suppression, await MigrationTestSql.ScalarAsync(stores.State,
                "SELECT json_group_array(json_object('scope',scope,'key',key,'reason',reason)) FROM suppressions"));
            Assert.Equal(3L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM revisions"));
            var result = Assert.Single((await Retrieval(stores).SearchAsync(new SearchRequest("quasar", "keyword"))).Results);
            Assert.Equal(id, result.DocumentId);
            Assert.Contains(result.Snippet, (await documents.GetAsync(id, true))!.Text!, StringComparison.Ordinal);
            Assert.Empty((await Retrieval(stores).SearchAsync(new SearchRequest("suppressed", "keyword"))).Results);
        }
    }

    [Fact]
    public async Task NewSpaceCannotReplaceActiveSpaceUntilEveryAuthoritativeRevisionHasCoverage()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        var documents = Documents(stores);
        var document = await documents.CaptureAsync(new CaptureRequest("original astronomy", "Space transition"), Key);
        var embeddings = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var oldSpace = await embeddings.EnsureSpaceAsync("old-space", "fixture", "old-model", 2);
        foreach (var work in await embeddings.PendingAsync(oldSpace.Id, 100))
            Assert.True(await embeddings.SaveAsync(oldSpace, work, work.Chunks.ToDictionary(chunk => chunk.Id, _ => new float[] { 1, 0 })));
        Assert.True(await embeddings.ActivateAsync(oldSpace.Id));
        var newSpace = await embeddings.EnsureSpaceAsync("new-space", "fixture", "new-model", 3);
        var beforeReplacement = Assert.Single(await embeddings.PendingAsync(newSpace.Id, 100));
        Assert.True(await embeddings.SaveAsync(newSpace, beforeReplacement, beforeReplacement.Chunks.ToDictionary(chunk => chunk.Id, _ => new float[] { 1, 0, 0 })));
        await Assert.ThrowsAsync<InjectedFailure>(() => Documents(stores, new CrashAt(CrashPointNames.AfterStatePublishing)).MutateAsync(
            document.Document.Id, 1, "update", JsonSerializer.SerializeToElement(new { content = "replacement astronomy" }), Session).AsTask());
        Assert.False(await embeddings.ActivateAsync(newSpace.Id));
        Assert.Equal(oldSpace.Id, (await embeddings.ActiveAsync())!.Id);
        await new PublicationCoordinator(stores.State, stores.Index).RecoverAsync();
        Assert.False(await embeddings.ActivateAsync(newSpace.Id));
        Assert.False(await embeddings.SaveAsync(newSpace, beforeReplacement, beforeReplacement.Chunks.ToDictionary(chunk => chunk.Id, _ => new float[] { 1, 0, 0 })));
        foreach (var work in await embeddings.PendingAsync(newSpace.Id, 100))
            Assert.True(await embeddings.SaveAsync(newSpace, work, work.Chunks.ToDictionary(chunk => chunk.Id, _ => new float[] { 1, 0, 0 })));
        Assert.True(await embeddings.ActivateAsync(newSpace.Id));
        Assert.Equal(newSpace.Id, (await embeddings.ActiveAsync())!.Id);
        Assert.Equal("indexed", (await documents.GetAsync(document.Document.Id))!.Status);
    }

    [Fact]
    public async Task RemovingSourceRemovesOnlyItsProjectionAndFencesLateExtraction()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        var documents = Documents(stores);
        var target = await documents.CaptureAsync(new CaptureRequest("target nebula", "Target"), Key);
        var other = await documents.CaptureAsync(new CaptureRequest("other nebula", "Other"), Key);
        var folder = new SecondBrain.Storage.Sources.SqliteFolderDocumentRepository(stores.State,
            new PublicationCoordinator(stores.State, stores.Index), documents);
        await folder.EnsureInboxAsync("/fixture/incoming/inbox");
        await MigrationTestSql.WriteAsync(stores.State, $"UPDATE documents SET source_id='inbox' WHERE id='{target.Document.Id}'");
        await folder.SetStatusAsync("inbox", "disabled", Session);
        Assert.Equal(2, (await Retrieval(stores).SearchAsync(new SearchRequest("nebula", "keyword"))).Results.Count);
        await folder.SetStatusAsync("inbox", "removed", Session);
        Assert.Equal(other.Document.Id, Assert.Single((await Retrieval(stores).SearchAsync(new SearchRequest("nebula", "keyword"))).Results).DocumentId);
        Assert.Equal("removed_from_source", (await documents.GetAsync(target.Document.Id))!.Status);
        await Assert.ThrowsAsync<IngestionConflictException>(() => documents.CompleteFileAsync(target.Document.Id, 1,
            new("extracted", "text", "text/plain", "late resurrection")).AsTask());
        Assert.Equal("cancelled", (await documents.JobsAsync()).Single(job => job.DocumentId == target.Document.Id).Status);
    }

    [Fact]
    public async Task BatchedFtsHighlightsPreserveRankedHitsAcrossExcludedRowsAndUnicode()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores); var documents = Documents(stores); var retrieval = Retrieval(stores);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var text in new[] { "red\u00A0\u00A0giant café telescope", "red giant cafe\u0301 telescopes", "red giant café telescopic" })
        {
            var capture = await documents.CaptureAsync(new CaptureRequest(text, "Target", "article"), Key);
            expected[capture.Document.Id] = text;
            await documents.CaptureAsync(new CaptureRequest("red giant café telescope", "Excluded"), Key);
        }
        foreach (var (query, wanted) in new[] { ("\"red giant\"", "red"), ("cafe", "caf"), ("telesc*", "telesc") })
        {
            var response = await retrieval.SearchAsync(new(query, "keyword", Filters: new RetrievalFilters(Type: "article")));
            Assert.Equal(3, response.Results.Count);
            Assert.Equal(response.Results.Select(hit => hit.ChunkId).Order(StringComparer.Ordinal), response.Results.Select(hit => hit.ChunkId));
            foreach (var hit in response.Results)
            {
                Assert.Equal(expected[hit.DocumentId], hit.Snippet);
                var range = Assert.Single(hit.HighlightRanges);
                Assert.StartsWith(wanted, hit.Snippet[range.Start..range.End]);
            }
        }
    }

    [Fact]
    public async Task FtsPhrasesPrefixesAndHighlightsUseMatchedTokenPositions()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores); var documents = Documents(stores); var retrieval = Retrieval(stores);
        var target = await documents.CaptureAsync(new CaptureRequest("red giant café telescope telescopes", "Target"), Key);
        await documents.CaptureAsync(new CaptureRequest("red distant giant", "Distractor"), Key);
        var phrase = Assert.Single((await retrieval.SearchAsync(new("\"red giant\"", "keyword"))).Results);
        Assert.Equal(target.Document.Id, phrase.DocumentId);
        Assert.Equal("red giant", phrase.Snippet[phrase.HighlightRanges[0].Start..phrase.HighlightRanges[0].End]);
        var accented = Assert.Single((await retrieval.SearchAsync(new("cafe", "keyword"))).Results);
        Assert.Equal("café", accented.Snippet[accented.HighlightRanges[0].Start..accented.HighlightRanges[0].End]);
        var prefix = Assert.Single((await retrieval.SearchAsync(new("telesc*", "keyword"))).Results);
        Assert.Equal(new[] { "telescope", "telescopes" }, prefix.HighlightRanges.Select(range => prefix.Snippet[range.Start..range.End]));
        Assert.Empty((await retrieval.SearchAsync(new("\" OR * () :", "keyword"))).Results);
    }

    [Fact]
    public async Task QueryCursorRejectsAnotherOperationOrInstance()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('instance_id','instance-one') ON CONFLICT(key) DO UPDATE SET value='instance-one'");
        for (var i = 0; i < 3; i++) await Documents(stores).CaptureAsync(new CaptureRequest("text", "Same title"), Key);
        var protection = new TestProtection();
        var retrieval = new SqliteRetrievalRepository(stores.Index, protection, state: stores.State);
        var request = new QueryRequest(Sort: "title", Direction: "asc", Limit: 1);
        var cursor = (await retrieval.QueryAsync(request, Key)).NextCursor!;
        const string Purpose = "SecondBrain.M1.QueryCursor.v1";
        var ticket = System.Text.Json.Nodes.JsonNode.Parse(protection.Unprotect(Purpose, cursor))!.AsObject();
        Assert.Equal("instance-one", (string?)ticket["Instance"]); Assert.Equal("query", (string?)ticket["Operation"]);
        var resigned = protection.Protect(Purpose, ticket.ToJsonString());
        Assert.Single((await retrieval.QueryAsync(request with { Cursor = resigned }, Key)).Records);
        ticket["Operation"] = "search";
        await Assert.ThrowsAsync<ArgumentException>(() => retrieval.QueryAsync(request with { Cursor = protection.Protect(Purpose, ticket.ToJsonString()) }, Key).AsTask());
        await MigrationTestSql.WriteAsync(stores.State, "UPDATE meta SET value='instance-two' WHERE key='instance_id'");
        await Assert.ThrowsAsync<ArgumentException>(() => retrieval.QueryAsync(request with { Cursor = cursor }, Key).AsTask());
    }

    [Fact]
    public async Task FtsSearchesTheNormalizedTextAndHighlightsMapToLosslessOffsets()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores); var documents = Documents(stores); var retrieval = Retrieval(stores);
        // The zero-width space and the whitespace run exist only in the lossless text; normalization removes and collapses them.
        var lossless = "first  line\n\n\tastro\u200Bnomy   and red\u00A0\u00A0giant";
        var target = await documents.CaptureAsync(new CaptureRequest(lossless, "Normalized"), Key);
        var word = Assert.Single((await retrieval.SearchAsync(new("astronomy", "keyword"))).Results);
        Assert.Equal(target.Document.Id, word.DocumentId);
        Assert.Equal("astro\u200Bnomy", word.Snippet[word.HighlightRanges[0].Start..word.HighlightRanges[0].End]);
        var reranked = await retrieval.SearchAsync(new("astronomy", "keyword", Rerank: "provider"));
        Assert.DoesNotContain("rerank", reranked.Degraded); // only the reranker reports its own failure
        var phrase = Assert.Single((await retrieval.SearchAsync(new("\"red giant\"", "keyword"))).Results);
        Assert.Equal("red\u00A0\u00A0giant", phrase.Snippet[phrase.HighlightRanges[0].Start..phrase.HighlightRanges[0].End]);
        Assert.Equal(lossless, (await documents.GetAsync(target.Document.Id, true))!.Text);
    }

    [Fact]
    public async Task NeighborsRespectBudgetRevisionDocumentAndSuppression()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores); var documents = Documents(stores); var retrieval = Retrieval(stores);
        var content = string.Join(" ", Enumerable.Repeat("astronomy observation", 150));
        var captured = await documents.CaptureAsync(new CaptureRequest(content, "Observations"), Key);
        await documents.CaptureAsync(new CaptureRequest(content, "Other"), Key);
        var hit = (await retrieval.SearchAsync(new("astronomy", "keyword", new RetrievalFilters(Predicates: [new("title", "eq", JsonSerializer.SerializeToElement("Observations"))])))).Results.First();
        var expanded = await retrieval.ExpandAsync([hit], 4096);
        Assert.True(expanded.Count >= 2); Assert.All(expanded, passage => Assert.Equal(captured.Document.Id, passage.DocumentId));
        var seed = expanded.Single(passage => passage.ChunkId == hit.ChunkId);
        var bounded = await retrieval.ExpandAsync([hit], seed.Tokens);
        Assert.Equal(hit.ChunkId, Assert.Single(bounded).ChunkId);
        Assert.Empty(await retrieval.ExpandAsync([hit with { Revision = 99 }], 4096));
        await documents.MutateAsync(captured.Document.Id, 1, "delete", JsonSerializer.SerializeToElement(new { }), Session);
        Assert.Empty(await retrieval.ExpandAsync([hit], 4096));
    }

    [Fact]
    public async Task TracesAreOptInBoundedAndDoNotResurrectDeletedLineage()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores); var documents = Documents(stores);
        var captured = await documents.CaptureAsync(new CaptureRequest("private astronomical data", "Observations"), Key);
        var options = new TraceOptions(); var retrieval = new SqliteRetrievalRepository(stores.Index, new TestProtection(), options);
        var request = new SearchRequest("astronomical", "keyword");
        var response = await retrieval.SearchAsync(request);
        Assert.Null(response.Trace);
        await retrieval.RecordTraceAsync(request, response);
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM retrieval_traces"));
        options.CurrentValue.Logging.RetrievalTrace = true;
        response = await retrieval.SearchAsync(request);
        for (var i = 0; i < 205; i++) await retrieval.RecordTraceAsync(request, response);
        Assert.Equal(200L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM retrieval_traces"));
        var raw = (string)(await MigrationTestSql.ScalarAsync(stores.Index, "SELECT query_json || trace_json FROM retrieval_traces LIMIT 1"))!;
        Assert.DoesNotContain("astronomical", raw, StringComparison.Ordinal);
        Assert.Contains(captured.Document.Id, raw, StringComparison.Ordinal);
        await documents.MutateAsync(captured.Document.Id, 1, "purge", JsonSerializer.SerializeToElement(new { }), Session);
        await retrieval.RecordTraceAsync(request, response);
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM retrieval_traces"));
    }

    [Fact]
    public async Task SemanticSelectionDiversifiesBeforeTopFiftyAndHonorsExactCap()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores); var documents = Documents(stores);
        await documents.CaptureAsync(new CaptureRequest(string.Join(" ", Enumerable.Repeat("astronomy observation", 150)), "Long"), Key);
        for (var i = 0; i < 4; i++) await documents.CaptureAsync(new CaptureRequest("astronomy", "Short " + i), Key);
        var embeddings = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var space = await embeddings.EnsureSpaceAsync("diversity", "fixture", "fixture", 2);
        foreach (var work in await embeddings.PendingAsync(space.Id, 100))
            Assert.True(await embeddings.SaveAsync(space, work, work.Chunks.ToDictionary(chunk => chunk.Id, _ => new float[] { 1, 0 })));
        Assert.True(await embeddings.ActivateAsync(space.Id));
        var options = new TraceOptions();
        Assert.Equal(1_000_000, options.CurrentValue.Retrieval.MaxExactCandidates);
        var retrieval = new SqliteRetrievalRepository(stores.Index, new TestProtection(), options);
        var result = await retrieval.SearchAsync(new("astronomy", "semantic", Limit: 20), [1, 0], space.Id);
        Assert.Equal(7, result.Results.Count);
        Assert.All(result.Results.GroupBy(hit => hit.DocumentId), group => Assert.InRange(group.Count(), 1, 3));
        options.CurrentValue.Retrieval.MaxExactCandidates = 2;
        var capped = await retrieval.SearchAsync(new("astronomy", "semantic", Limit: 20), [1, 0], space.Id);
        Assert.Contains("exact-cap", capped.Degraded);
        Assert.Equal(2, capped.Results.Count);
    }

    [Fact]
    public async Task PartialVectorCoverageIsReportedEvenWhenOtherDocumentsMatch()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores); var documents = Documents(stores);
        await documents.CaptureAsync(new CaptureRequest("astronomy observation", "Ready"), Key);
        var embeddings = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var space = await embeddings.EnsureSpaceAsync("partial", "fixture", "fixture", 2);
        var work = Assert.Single(await embeddings.PendingAsync(space.Id));
        await embeddings.SaveAsync(space, work, work.Chunks.ToDictionary(chunk => chunk.Id, _ => new float[] { 1, 0 }));
        Assert.True(await embeddings.ActivateAsync(space.Id));
        await documents.CaptureAsync(new CaptureRequest("astronomy pending", "Pending"), Key);
        var response = await Retrieval(stores).SearchAsync(new("astronomy", "hybrid"), [1, 0], space.Id);
        Assert.Equal(2, response.Results.Count);
        Assert.Contains("vector-pending", response.Degraded);
        var filtered = await Retrieval(stores).SearchAsync(new("astronomy", "semantic", new RetrievalFilters(Predicates: [new("title", "eq", JsonSerializer.SerializeToElement("Ready"))])), [1, 0], space.Id);
        Assert.DoesNotContain("vector-pending", filtered.Degraded);
    }

    [Fact]
    public async Task BulkDirtyRefreshUsesBoundedReadsAndReplacesVectorsLocatorsAndPendingCoverage()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.Index, """
            WITH RECURSIVE n(x) AS(VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<299)
            INSERT INTO indexed_documents(document_id,revision,type,title,props_json,status,updated_at)
            SELECT 'doc-'||x,1,'note','Bulk','{}','indexed','2026-10-09' FROM n;
            WITH RECURSIVE n(x) AS(VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<299)
            INSERT INTO chunks(rowid,id,document_id,revision,ordinal,context,text,start_offset,end_offset,token_count,input_hash)
            SELECT x+1,'chunk-'||x,'doc-'||x,1,0,'','text',0,4,5,'hash'||x FROM n;
            INSERT INTO chunk_vectors(space_id,chunk_rowid,vector)
            SELECT 'fixture',rowid,X'0000803F00000000' FROM chunks WHERE document_id<>'doc-2';
            """);
        var registry = VectorSnapshotRegistry.For(stores.Index);
        using (await registry.EnterAsync())
        await using (var lease = await stores.Index.OpenReadConnectionAsync())
        await using (var transaction = await lease.Connection.BeginTransactionAsync())
        using (var vectors = await registry.AcquireAsync(lease.Connection, transaction, "fixture", 2))
        {
            Assert.Equal(299, vectors.Count);
            Assert.True(registry.HasMissingVectors("doc-2"));
        }
        await MigrationTestSql.WriteAsync(stores.Index, """
            UPDATE chunk_vectors SET vector=X'000000000000803F' WHERE chunk_rowid=1;
            DELETE FROM chunk_vectors WHERE chunk_rowid=2;
            DELETE FROM chunks WHERE rowid=2;
            DELETE FROM indexed_documents WHERE document_id='doc-1';
            INSERT INTO chunk_vectors(space_id,chunk_rowid,vector) VALUES('fixture',3,X'0000803F00000000');
            UPDATE chunks SET id='replacement-chunk',ordinal=7 WHERE rowid=4;
            DELETE FROM chunk_vectors WHERE chunk_rowid=5;
            """);
        using (await registry.EnterAsync())
        {
            for (var i = 0; i < 300; i++) registry.Committed("doc-" + i);
            var statements = registry.VectorReadStatements;
            await using var lease = await stores.Index.OpenReadConnectionAsync();
            await using var transaction = await lease.Connection.BeginTransactionAsync();
            using var vectors = await registry.AcquireAsync(lease.Connection, transaction, "fixture", 2);
            Assert.Equal(1, registry.VectorReadStatements - statements);
            Assert.Equal(298, vectors.Count);
            Assert.True(vectors.TryGetVector("chunk-0", out var updated));
            Assert.Equal(new float[] { 0, 1 }, updated.ToArray());
            Assert.False(vectors.TryGetVector("chunk-1", out _));
            Assert.False(vectors.TryGetVector("chunk-3", out _));
            Assert.Equal(new[] { "replacement-chunk" }, registry.ChunkIds("doc-3"));
            Assert.Empty(registry.ChunkIds("doc-1"));
            Assert.False(registry.HasMissingVectors("doc-2"));
            Assert.True(registry.HasMissingVectors("doc-4"));
        }
    }

    [Fact]
    public async Task VectorReloadCrossesBatchesAndOrdersSparseRowsByChunkOrdinal()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.Index, """
            INSERT INTO indexed_documents(document_id,revision,type,title,props_json,status,updated_at)
            VALUES('doc0',1,'note','Scale','{}','indexed','2026-10-09'),('doc1',1,'note','Scale','{}','indexed','2026-10-09'),
                  ('doc2',1,'note','Scale','{}','indexed','2026-10-09'),('doc3',1,'note','Scale','{}','indexed','2026-10-09');
            WITH RECURSIVE n(x) AS(VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<19999)
            INSERT INTO chunks(rowid,id,document_id,revision,ordinal,context,text,start_offset,end_offset,token_count,input_hash)
            SELECT 9000000000000000000-x*100,printf('chunk-%05d',x),'doc'||(x/5000),1,x%5000,'','text',0,4,5,'hash'||x FROM n;
            INSERT INTO chunk_vectors(space_id,chunk_rowid,vector) SELECT 'fixture',rowid,X'0000803F00000000' FROM chunks;
            """);
        var options = new TraceOptions(); options.CurrentValue.Retrieval.MaxExactCandidates = 2;
        var result = await new SqliteRetrievalRepository(stores.Index, new TestProtection(), options).SearchAsync(new("text", "semantic"), [1, 0], "fixture");
        Assert.Equal(new[] { "chunk-00000", "chunk-00001" }, result.Results.Select(hit => hit.ChunkId));
        Assert.Contains("exact-cap", result.Degraded);
    }

    [Fact]
    public async Task PartitionedExactScoringMatchesIndependentDiverseOracleWithTiesFiltersAndCap()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.Index, """
            WITH RECURSIVE n(x) AS(VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<10009)
            INSERT INTO indexed_documents(document_id,revision,type,title,props_json,status,updated_at)
            SELECT printf('doc-%05d',x),1,CASE WHEN x%2=0 THEN 'article' ELSE 'note' END,'Scoring fixture','{}','indexed','2026-10-09' FROM n;
            WITH RECURSIVE n(x) AS(VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<100099)
            INSERT INTO chunks(id,document_id,revision,ordinal,context,text,start_offset,end_offset,token_count,input_hash)
            SELECT printf('chunk-%06d',x),printf('doc-%05d',x/10),1,x%10,'','text',0,4,5,'hash'||x FROM n;
            INSERT INTO chunk_vectors(space_id,chunk_rowid,vector)
            SELECT 'fixture',rowid,CASE ordinal%4 WHEN 0 THEN X'0000803F00000000' WHEN 1 THEN X'000000000000803F' WHEN 2 THEN X'000080BF00000000' ELSE X'00000000000080BF' END FROM chunks;
            """);
        var options = new TraceOptions(); options.CurrentValue.Logging.RetrievalTrace = true;
        var retrieval = new SqliteRetrievalRepository(stores.Index, new TestProtection(), options);
        foreach (var filtered in new[] { false, true })
        foreach (var cap in new[] { 1_000_000, 100_001 })
        {
            options.CurrentValue.Retrieval.MaxExactCandidates = cap;
            var eligible = Enumerable.Range(0, 100100).Where(i => !filtered || i / 10 % 2 == 0).Take(cap);
            var expected = eligible.Select(i => new { Id = $"chunk-{i:D6}", Document = i / 10, Score = (i % 10 % 4) switch { 0 => 1, 2 => -1, _ => 0 } })
                .GroupBy(item => item.Document).SelectMany(group => group.OrderByDescending(item => item.Score).ThenBy(item => item.Id, StringComparer.Ordinal).Take(3))
                .OrderByDescending(item => item.Score).ThenBy(item => item.Id, StringComparer.Ordinal).Take(50).Select(item => item.Id).ToArray();
            var response = await retrieval.SearchAsync(new("text", "semantic", filtered ? new RetrievalFilters(Type: "article") : null, Limit: 50), [1, 0], "fixture");
            Assert.Equal(expected, response.Trace!.Semantic.Select(item => item.ChunkId));
            Assert.Equal(50, response.Results.Count);
            Assert.Equal(!filtered && cap == 100_001, response.Degraded.Contains("exact-cap"));
        }
    }

    [Fact]
    public async Task ActiveSpaceRepairsStatusAfterEmbeddingIndexCommitWithoutReembeddingOrVectorReload()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        var embeddings = new SqliteEmbeddingWorkRepository(stores.State, stores.Index);
        var space = await embeddings.EnsureSpaceAsync("repair", "fixture", "repair", 2);
        Assert.True(await embeddings.ActivateAsync(space.Id));
        var document = await Documents(stores).CaptureAsync(new CaptureRequest("repair observation", "Repair"), Key);
        var pending = Assert.Single(await embeddings.PendingAsync(space.Id));
        var interrupted = new SqliteEmbeddingWorkRepository(new FailNextStateWrite(stores.State), stores.Index);
        await Assert.ThrowsAsync<InjectedFailure>(() => interrupted.SaveAsync(space, pending,
            pending.Chunks.ToDictionary(chunk => chunk.Id, _ => new float[] { 1, 0 })).AsTask());
        Assert.Equal("indexed", await MigrationTestSql.ScalarAsync(stores.Index, "SELECT status FROM indexed_documents"));
        Assert.Equal("indexed_partial", (await Documents(stores).GetAsync(document.Document.Id))!.Status);
        Assert.Empty(await embeddings.PendingAsync(space.Id));
        var retrieval = Retrieval(stores);
        await retrieval.SearchAsync(new("repair", "semantic"), [1, 0], space.Id);
        var registry = VectorSnapshotRegistry.For(stores.Index); var reads = registry.VectorRowsRead;
        await embeddings.ReconcileActiveAsync(space.Id);
        Assert.Equal("indexed", (await Documents(stores).GetAsync(document.Document.Id))!.Status);
        await retrieval.SearchAsync(new("repair", "semantic"), [1, 0], space.Id);
        Assert.Equal(reads, registry.VectorRowsRead);
    }

    [Fact]
    public async Task FailedBatchedVectorLoadCannotRetainOrphanSlotsAfterDeletion()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        var registry = VectorSnapshotRegistry.For(stores.Index);
        var retrieval = Retrieval(stores);
        Assert.Empty((await retrieval.SearchAsync(new("text", "semantic"), [1, 0], "fixture")).Results);
        await MigrationTestSql.WriteAsync(stores.Index, """
            INSERT INTO indexed_documents(document_id,revision,type,title,props_json,status,updated_at) VALUES('doc',1,'note','Fault fixture','{}','indexed','2026-10-09');
            WITH RECURSIVE n(x) AS(VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<16384)
            INSERT INTO chunks(rowid,id,document_id,revision,ordinal,context,text,start_offset,end_offset,token_count,input_hash)
            SELECT x+1,'chunk-'||x,'doc',1,x,'','text',0,4,5,'hash'||x FROM n;
            INSERT INTO chunk_vectors(space_id,chunk_rowid,vector) SELECT 'fixture',rowid,CASE WHEN rowid=16385 THEN X'00' ELSE X'0000803F00000000' END FROM chunks;
            """);
        using (await registry.EnterAsync()) registry.Committed("doc");
        await Assert.ThrowsAsync<InvalidDataException>(() => retrieval.SearchAsync(new("text", "semantic"), [1, 0], "fixture").AsTask());
        using (await registry.EnterAsync())
        {
            await MigrationTestSql.WriteAsync(stores.Index, "DELETE FROM chunk_vectors WHERE chunk_rowid IN (SELECT rowid FROM chunks WHERE document_id='doc'); DELETE FROM chunks WHERE document_id='doc'; DELETE FROM indexed_documents WHERE document_id='doc'");
            registry.Committed("doc");
        }
        Assert.Empty((await retrieval.SearchAsync(new("text", "semantic"), [1, 0], "fixture")).Results);
        using (await registry.EnterAsync())
        await using (var lease = await stores.Index.OpenReadConnectionAsync())
        await using (var transaction = await lease.Connection.BeginTransactionAsync())
        using (var vectors = await registry.AcquireAsync(lease.Connection, transaction, "fixture", 2))
            Assert.Equal(0, vectors.Count);
    }

    private sealed class FailNextStateWrite(IStateStore inner) : IStateStore
    {
        private bool failed;
        public ValueTask<IReadConnectionLease> OpenReadConnectionAsync(CancellationToken cancellationToken = default) => inner.OpenReadConnectionAsync(cancellationToken);
        public ValueTask<TResult> QueueWriteAsync<TResult>(Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, CancellationToken, ValueTask<TResult>> write, CancellationToken cancellationToken = default)
        {
            if (!failed) { failed = true; throw new InjectedFailure(); }
            return inner.QueueWriteAsync(write, cancellationToken);
        }
    }

    private sealed class TraceOptions : Microsoft.Extensions.Options.IOptionsMonitor<SecondBrain.Core.Configuration.SecondBrainOptions>
    {
        public SecondBrain.Core.Configuration.SecondBrainOptions CurrentValue { get; } = new();
        public SecondBrain.Core.Configuration.SecondBrainOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SecondBrain.Core.Configuration.SecondBrainOptions, string?> listener) => null;
    }

    private static SqliteDocumentRepository Documents(MigrationTestStores stores, ICrashPoints? crash = null) =>
        new(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index, crash));
    private static SqliteRetrievalRepository Retrieval(MigrationTestStores stores) => new(stores.Index, new TestProtection());

    private static async Task SeedAsync(MigrationTestStores stores)
    {
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
        var auth = new AuthRepository(stores.State, TimeProvider.System);
        foreach (var actor in new[] { Key, Session })
            await auth.AddAsync(new CredentialRecord { Id = actor.CredentialId, Name = actor.CredentialName, Kind = actor.Kind,
                AccountEpoch = 1, Generation = 1, Scopes = "read,write,admin", Verifier = "fixture", Kid = "fixture",
                SteppedUpAt = AuthTime.Format(DateTimeOffset.UtcNow), CreatedAt = AuthTime.Format(DateTimeOffset.UtcNow) });
    }

    // Storage cursor binding is the subject here; authenticated protection is covered by Server.
    private sealed class TestProtection : IPayloadProtection
    {
        public string Protect(string purpose, string plaintext) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(purpose + "\n" + plaintext));
        public string Unprotect(string purpose, string protectedText) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText))[(purpose.Length + 1)..];
    }
    private sealed class InjectedFailure : Exception;
    private sealed class CrashAt(string point) : ICrashPoints
    {
        public ValueTask HitAsync(string observed, CancellationToken cancellationToken = default) => observed == point ? throw new InjectedFailure() : ValueTask.CompletedTask;
    }
}
