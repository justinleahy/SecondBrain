using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecondBrain.Core.Extraction;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Tests.Support;
using SecondBrain.MockProvider;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class DocumentsApiTests
{
    [Theory]
    [InlineData("{\"type\":\"calendar_event\"}")]
    [InlineData("{\"predicates\":[{\"field\":\"title\",\"op\":\"wat\",\"value\":\"x\"}]}")]
    [InlineData("{\"predicates\":[{\"field\":\"props.secret\",\"op\":\"eq\",\"value\":\"x\"}]}")]
    [InlineData("{\"predicates\":[{\"field\":\"title\",\"op\":\"in\",\"value\":\"x\"}]}")]
    [InlineData("{\"predicates\":[{\"field\":\"title\",\"op\":\"in\",\"value\":[{}]}]}")]
    public async Task InvalidSemanticFiltersAreRejectedBeforeProviderWork(string filters)
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory, Scope.Read);
        var provider = factory.MockProvider.Services.GetRequiredService<MockProviderState>();
        var before = provider.GetRequestCount("/v1/embeddings");
        using var response = await client.PostAsync("/v1/search", new StringContent("{\"query\":\"private topic\",\"mode\":\"semantic\",\"filters\":" + filters + "}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, provider.GetRequestCount("/v1/embeddings"));
    }

    [Fact]
    public async Task ExtractionDeadlineIsDurableNamedBoundAndIsNotRetried()
    {
        var extractor = new DeadlineExtractor();
        await using var factory = new LaneDWebFactory(configureServices: services =>
        {
            services.RemoveAll<IExtractorClient>(); services.AddSingleton<IExtractorClient>(extractor);
        });
        using var client = await ClientAsync(factory, Scope.Write, Scope.Read);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("deadline fixture")), "file", "deadline.txt");
        using var response = await client.PostAsync("/v1/documents?wait=5", form);
        var captured = (await response.Content.ReadFromJsonAsync<CaptureResult>())!;
        Assert.Equal("skipped", captured.Document.Status);
        var job = (await client.GetFromJsonAsync<ProcessingJob>("/v1/jobs/" + captured.Job!.Id))!;
        Assert.Equal("failed", job.Status); Assert.Equal("extraction_deadline", job.Error); Assert.Equal(1, job.Attempts);
        await Task.Delay(2300);
        Assert.Equal(1, extractor.Calls);
        using var retry = await client.PostAsync("/v1/jobs/" + job.Id + "/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
    }

    private sealed class DeadlineExtractor : IExtractorClient
    {
        public int Calls;
        public ValueTask<SecondBrain.Extraction.Contracts.ExtractionResult> ExtractAsync(Stream stagedInput, string formatHint, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult(new SecondBrain.Extraction.Contracts.ExtractionResult("skipped", "unknown", "application/octet-stream", "", Error: "bound_exceeded", Bound: "extraction_deadline"));
        }
    }

    [Fact]
    public async Task FileQueueUsesFourBoundedWorkersAndDrainsWithoutPerFilePollingDelay()
    {
        var extractor = new FourWorkerExtractor();
        await using var factory = new LaneDWebFactory(configureServices: services =>
        {
            services.RemoveAll<IExtractorClient>();
            services.AddSingleton<IExtractorClient>(extractor);
        });
        using var client = await ClientAsync(factory, Scope.Write, Scope.Read);
        var ids = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("bounded worker fixture " + i)), "file", "worker-" + i + ".txt");
            using var admitted = await client.PostAsync("/v1/documents", form);
            Assert.Equal(HttpStatusCode.Created, admitted.StatusCode);
            ids.Add((await admitted.Content.ReadFromJsonAsync<CaptureResult>())!.Document.Id);
        }
        try
        {
            await extractor.FourEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, extractor.MaximumActive);
        }
        finally { extractor.Release.TrySetResult(); }
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var jobs = (await client.GetFromJsonAsync<ProcessingJob[]>("/v1/jobs"))!;
            if (ids.All(id => jobs.Any(job => job.DocumentId == id && job.Stage == "extract" && job.Status == "completed"))) break;
            await Task.Delay(50);
        }
        foreach (var id in ids)
            Assert.Contains((await client.GetFromJsonAsync<ProcessingJob[]>("/v1/jobs"))!, job => job.DocumentId == id && job.Stage == "extract" && job.Status == "completed");
        Assert.Equal(4, extractor.MaximumActive);
    }

    private sealed class FourWorkerExtractor : IExtractorClient
    {
        private int active;
        private int maximum;
        public int MaximumActive => Volatile.Read(ref maximum);
        public TaskCompletionSource FourEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<SecondBrain.Extraction.Contracts.ExtractionResult> ExtractAsync(Stream stagedInput, string formatHint, CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref active);
            int observed;
            do { observed = Volatile.Read(ref maximum); }
            while (observed < current && Interlocked.CompareExchange(ref maximum, current, observed) != observed);
            if (current == 4) FourEntered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                using var reader = new StreamReader(stagedInput, leaveOpen: true);
                return new("extracted", "text", "text/plain", await reader.ReadToEndAsync(cancellationToken));
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }

    [Fact]
    public async Task CaptureSearchQueryUseCredentialProvenanceAndProjectionContracts()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory, Scope.Write, Scope.Read);
        using var capture = await client.PostAsJsonAsync("/v1/documents", new CaptureRequest("A memorable otter swims.", "Otter", Tags: ["wildlife"]));
        Assert.Equal(HttpStatusCode.Created, capture.StatusCode);
        var admitted = (await capture.Content.ReadFromJsonAsync<CaptureResult>())!;
        Assert.Equal("api:ingest-test", admitted.Document.Provenance);
        Assert.Equal("live", admitted.Keyword);
        Assert.NotNull(admitted.Job);
        using var search = await client.PostAsJsonAsync("/v1/search", new SearchRequest("otter", "keyword", new RetrievalFilters(Type: "note")));
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        var results = (await search.Content.ReadFromJsonAsync<SearchResponse>())!;
        Assert.Contains(results.Results, hit => hit.DocumentId == admitted.Document.Id);
        using var query = await client.PostAsJsonAsync("/v1/query", new QueryRequest(Type: "note", Tags: ["wildlife"]));
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        Assert.Contains((await query.Content.ReadFromJsonAsync<QueryPage>())!.Records, doc => doc.Id == admitted.Document.Id);
        var read = await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + admitted.Document.Id + "?include=text");
        Assert.Equal("A memorable otter swims.", read!.Text);
        Assert.Contains((await client.GetFromJsonAsync<ProcessingJob[]>("/v1/jobs"))!, job => job.DocumentId == admitted.Document.Id);
    }

    [Fact]
    public async Task MutationsRequireCurrentRevisionAndKeysCannotPurge()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory, Scope.Write, Scope.Read, Scope.Admin);
        using var captured = await client.PostAsJsonAsync("/v1/documents", new CaptureRequest("Revision guarded", "Guard"));
        var result = (await captured.Content.ReadFromJsonAsync<CaptureResult>())!;
        Assert.Equal("\"1\"", captured.Headers.ETag!.Tag);
        using var stale = new HttpRequestMessage(HttpMethod.Patch, "/v1/documents/" + result.Document.Id) { Content = JsonContent.Create(new { title = "Changed" }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"99\"");
        using var response = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, problem.RootElement.GetProperty("current_revision").GetInt64());
        using var purge = await client.DeleteAsync("/v1/documents/" + result.Document.Id + "?purge=1");
        Assert.Equal(HttpStatusCode.Forbidden, purge.StatusCode);
        var unchanged = await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + result.Document.Id);
        Assert.Equal("Guard", unchanged!.Title);
    }

    [Fact]
    public async Task IdempotencyReturnsOriginalAndRejectsDifferentPayload()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory, Scope.Write);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "same-capture");
        var request = new CaptureRequest("once", "Capture");
        using var first = await client.PostAsJsonAsync("/v1/documents", request);
        using var repeated = await client.PostAsJsonAsync("/v1/documents", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<CaptureResult>())!.Document.Id, (await repeated.Content.ReadFromJsonAsync<CaptureResult>())!.Document.Id);
        using var conflict = await client.PostAsJsonAsync("/v1/documents", request with { Content = "changed" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflict.StatusCode);
        Assert.Contains("idempotency-key-reuse", await conflict.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultipartCaptureUsesSandboxAndCleansPrivateStage()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory, Scope.Write, Scope.Read);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("{\"content\":\"\",\"title\":\"Uploaded\"}"), "metadata");
        multipart.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("# Header\n\nThe uploaded capybara.")), "file", "sample.md");
        using var capture = await client.PostAsync("/v1/documents?wait=5", multipart);
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        var result = (await capture.Content.ReadFromJsonAsync<CaptureResult>())!;
        Assert.Equal("Uploaded", result.Document.Title);
        using var download = await client.GetAsync("/v1/files/" + result.Document.Id);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("# Header\n\nThe uploaded capybara.", await download.Content.ReadAsStringAsync());
        Assert.Contains("sandbox", download.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        var read = await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + result.Document.Id + "?include=text");
        Assert.Contains("capybara", read!.Text, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(factory.Options.CurrentValue.DataRoot, "staging"), "upload-*"));
    }

    [Theory]
    [InlineData("/v1/documents", "{\"content\":\"text\",\"source\":\"spoof\"}")]
    [InlineData("/v1/search", "{\"query\":\"topic\",\"mode\":\"bad\"}")]
    [InlineData("/v1/query", "{\"limit\":201}")]
    [InlineData("/v1/query", "{\"type\":\"calendar_event\"}")]
    [InlineData("/v1/query", "{\"occurred\":{\"from\":\"not-a-date\"}}")]
    [InlineData("/v1/query", "{\"updated\":{\"to\":\"2026-99-99\"}}")]
    [InlineData("/v1/query", "{\"people\":[null]}")]
    [InlineData("/v1/query", "{\"people\":[{\"value\":null}]}")]
    [InlineData("/v1/query", "{\"predicates\":[null]}")]
    [InlineData("/v1/query", "{\"predicates\":[{\"field\":null,\"op\":\"eq\",\"value\":1}]}")]
    [InlineData("/v1/search", "{\"query\":\"topic\",\"filters\":{\"occurred\":{\"from\":\"bad\"}}}")]
    [InlineData("/v1/search", "{\"query\":\"topic\",\"filters\":{\"people\":[null]}}")]
    [InlineData("/v1/search", "{\"query\":\"topic\",\"filters\":{\"predicates\":[null]}}")]
    [InlineData("/v1/query", "{\"predicates\":[{\"field\":\"props.secret\",\"op\":\"eq\",\"value\":\"x\"}]}")]
    public async Task InvalidContractsReturnProblem(string route, string json)
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory, Scope.Write, Scope.Read);
        using var response = await client.PostAsync(route, new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task KeyUpdateCreatesProposalAndDoesNotChangeBody()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory, Scope.Write, Scope.Read);
        using var admitted = await client.PostAsJsonAsync("/v1/documents", new CaptureRequest("original", "Original"));
        var doc = (await admitted.Content.ReadFromJsonAsync<CaptureResult>())!.Document;
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/v1/documents/" + doc.Id) { Content = JsonContent.Create(new { content = "replacement" }) };
        request.Headers.IfMatch.ParseAdd("\"" + doc.Revision + "\"");
        using var proposal = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, proposal.StatusCode);
        Assert.False(string.IsNullOrEmpty((await proposal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pending_id").GetString()));
        var read = await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + doc.Id + "?include=text");
        Assert.Equal("original", read!.Text);
        Assert.Equal(doc.Revision, read.Revision);
    }

    [Fact]
    public async Task IndependentScopesAndRevocationAreEnforced()
    {
        await using var factory = new LaneDWebFactory();
        using var reader = await ClientAsync(factory, Scope.Read);
        using var denied = await reader.PostAsJsonAsync("/v1/documents", new CaptureRequest("not allowed"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var writer = await ClientAsync(factory, Scope.Write);
        using var search = await writer.PostAsJsonAsync("/v1/search", new SearchRequest("anything"));
        Assert.Equal(HttpStatusCode.Forbidden, search.StatusCode);
        using var anonymous = factory.CreatePrivateClient();
        anonymous.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var noAuth = await anonymous.PostAsJsonAsync("/v1/query", new QueryRequest());
        Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);
    }

    [Fact]
    public async Task SemanticSearchUsesConfiguredProviderAndOutageFallsBackToKeyword()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory, Scope.Read, Scope.Write);
        using var admitted = await client.PostAsJsonAsync("/v1/documents", new CaptureRequest("The mongoose remembers.", "Mongoose"));
        Assert.Equal(HttpStatusCode.Created, admitted.StatusCode);
        var mock = factory.MockProvider.Services.GetRequiredService<MockProviderState>();
        Assert.NotNull(await factory.Services.GetRequiredService<IEmbeddingWorkRepository>().ActiveAsync());
        var count = mock.GetRequestCount("/v1/embeddings");
        using var semantic = await client.PostAsJsonAsync("/v1/search", new SearchRequest("mongoose", "semantic"));
        Assert.Equal(HttpStatusCode.OK, semantic.StatusCode);
        Assert.True(mock.GetRequestCount("/v1/embeddings") > count);
        Assert.NotEmpty((await semantic.Content.ReadFromJsonAsync<SearchResponse>())!.Results);
        mock.Fault = MockFault.RateLimit;
        using var fallback = await client.PostAsJsonAsync("/v1/search", new SearchRequest("mongoose", "semantic"));
        Assert.Equal(HttpStatusCode.OK, fallback.StatusCode);
        var results = (await fallback.Content.ReadFromJsonAsync<SearchResponse>())!;
        Assert.NotEmpty(results.Results);
        Assert.Contains("embed-unavailable", results.Degraded);
        var beforeQuery = mock.TotalRequests;
        using var query = await client.PostAsJsonAsync("/v1/query", new QueryRequest(Type: "note"));
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        Assert.Equal(beforeQuery, mock.TotalRequests);
    }

    [Fact]
    public async Task QueryCursorRejectsDifferentQueryCredentialAndRevocation()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory, Scope.Read, Scope.Write);
        var auth = factory.Services.GetRequiredService<IAuthRepository>();
        var identity = Assert.Single((await auth.ListPageAsync("api_key", true, null, 100)).Items);
        for (var i = 0; i < 3; i++)
        {
            using var capture = await client.PostAsJsonAsync("/v1/documents", new CaptureRequest("record " + i, "Record " + i));
            Assert.Equal(HttpStatusCode.Created, capture.StatusCode);
        }
        var request = new QueryRequest(Type: "note", Sort: "title", Direction: "asc", Limit: 1);
        using var first = await client.PostAsJsonAsync("/v1/query", request);
        var cursor = (await first.Content.ReadFromJsonAsync<QueryPage>())!.NextCursor;
        Assert.NotNull(cursor);
        using var next = await client.PostAsJsonAsync("/v1/query", request with { Cursor = cursor });
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        using var changed = await client.PostAsJsonAsync("/v1/query", request with { Cursor = cursor, Type = "article" });
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
        using var other = await ClientAsync(factory, Scope.Read);
        using var reused = await other.PostAsJsonAsync("/v1/query", request with { Cursor = cursor });
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        Assert.True(await auth.RevokeAsync(identity.Id));
        using var revoked = await client.PostAsJsonAsync("/v1/query", request with { Cursor = cursor });
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task UploadIsDurableBeforeExtractionAndSurvivesRequestCompletion()
    {
        var extractor = new BarrierExtractor();
        await using var factory = new LaneDWebFactory(configureServices: services => services.Replace(ServiceDescriptor.Singleton<IExtractorClient>(extractor)));
        using var client = await ClientAsync(factory, Scope.Read, Scope.Write);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("original body")), "file", "admitted.txt");
        using var response = await client.PostAsync("/v1/documents", multipart).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<CaptureResult>())!;
        Assert.Equal("queued", result.Document.Status);
        Assert.NotNull(result.Job);
        Assert.NotNull(await factory.Services.GetRequiredService<IDocumentRepository>().GetAsync(result.Document.Id));
        extractor.Complete(new ExtractionResult("failed", "text", "text/plain", "", Error: "malformed-document"));
        DocumentRecord? completed = null;
        for (var i = 0; i < 100; i++)
        {
            completed = await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + result.Document.Id);
            if (completed!.Status == "failed") break;
            await Task.Delay(50);
        }
        Assert.Equal("failed", completed!.Status);
        using var search = await client.PostAsJsonAsync("/v1/search", new SearchRequest("admitted", "keyword"));
        Assert.Contains((await search.Content.ReadFromJsonAsync<SearchResponse>())!.Results, hit => hit.DocumentId == result.Document.Id);
    }

    [Fact]
    public async Task IdenticalOriginalsReuseExtractionCacheWithoutSharingDocumentIdentity()
    {
        var extractor = new CountingExtractor();
        await using var factory = new LaneDWebFactory(configureServices: services => services.Replace(ServiceDescriptor.Singleton<IExtractorClient>(extractor)));
        using var client = await ClientAsync(factory, Scope.Read, Scope.Write);
        var ids = new List<string>();
        foreach (var title in new[] { "First immutable original", "Second immutable original" })
        {
            using var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(JsonSerializer.Serialize(new { content = "", title })), "metadata");
            multipart.Add(new ByteArrayContent("Cacheable monotreme bytes"u8.ToArray()), "file", "same.txt");
            using var response = await client.PostAsync("/v1/documents?wait=5", multipart);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<CaptureResult>())!;
            ids.Add(result.Document.Id);
            Assert.Equal(title, result.Document.Title);
        }
        Assert.NotEqual(ids[0], ids[1]);
        Assert.Equal(1, extractor.Calls);
        using var search = await client.PostAsJsonAsync("/v1/search", new SearchRequest("monotreme", "keyword"));
        Assert.Equal(2, (await search.Content.ReadFromJsonAsync<SearchResponse>())!.Results.Select(hit => hit.DocumentId).Distinct().Count());
    }

    private sealed class CountingExtractor : IExtractorClient
    {
        public int Calls { get; private set; }
        public ValueTask<ExtractionResult> ExtractAsync(Stream input, string hint, CancellationToken token = default)
        {
            Calls++;
            return ValueTask.FromResult(new ExtractionResult("extracted", "text", "text/plain", "Cacheable monotreme bytes"));
        }
    }

    private sealed class BarrierExtractor : IExtractorClient
    {
        private readonly TaskCompletionSource<ExtractionResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(ExtractionResult result) => completion.TrySetResult(result);
        public async ValueTask<ExtractionResult> ExtractAsync(Stream stagedInput, string formatHint, CancellationToken cancellationToken = default) => await completion.Task.WaitAsync(cancellationToken);
    }

    [Fact]
    public async Task UnavailableDocumentsNeverServeRetainedTextThroughAnyReadForm()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var writer = await ClientAsync(factory, Scope.Write, Scope.Read);
        using var reader = await ClientAsync(factory, Scope.Read);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var state in new[] { "deleted", "missing", "removed_from_source", "purged", "indexed" })
        {
            using var captured = await writer.PostAsJsonAsync("/v1/documents", new CaptureRequest($"private {state} plaintext body", $"private {state} title", Tags: ["secret-tag"]));
            ids[state] = (await captured.Content.ReadFromJsonAsync<CaptureResult>())!.Document.Id;
        }
        // Deletion goes through the real session lifecycle; the other states are written as scans and purge leave them.
        using var session = factory.CreatePrivateClient();
        session.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using (var login = await session.PostAsJsonAsync("/auth/login", new LoginRequest("test-password"))) Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var csrf = (await session.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        session.DefaultRequestHeaders.Add(csrf.HeaderName, csrf.Token);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/v1/documents/" + ids["deleted"]);
        delete.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using (var deleted = await session.SendAsync(delete))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            Assert.DoesNotContain("plaintext body", await deleted.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        await using (var lease = await factory.Store.OpenReadConnectionAsync())
            Assert.Equal("deleted", await Dapper.SqlMapper.QuerySingleAsync<string>(lease.Connection, "SELECT status FROM documents WHERE id=@id", new { id = ids["deleted"] }));
        await factory.Store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            foreach (var state in new[] { "missing", "removed_from_source", "purged" })
                await Dapper.SqlMapper.ExecuteAsync(connection, "UPDATE documents SET status=@state WHERE id=@id", new { state, id = ids[state] }, transaction);
            return 0;
        });
        foreach (var state in new[] { "deleted", "missing", "removed_from_source", "purged" })
            foreach (var prefix in new[] { "", "/v1" })
                foreach (var include in new[] { "text", "chunks", "text,chunks", "chunks,text" })
                {
                    using var read = await reader.GetAsync($"{prefix}/documents/{ids[state]}?include={include}");
                    Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
                    Assert.DoesNotContain("plaintext body", await read.Content.ReadAsStringAsync(), StringComparison.Ordinal);
                }
        // Lifecycle responses about an unavailable document carry no retained projection either.
        using var again = new HttpRequestMessage(HttpMethod.Delete, "/v1/documents/" + ids["deleted"]);
        again.Headers.TryAddWithoutValidation("If-Match", "\"2\"");
        using (var repeated = await session.SendAsync(again))
        {
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            Assert.DoesNotContain("plaintext body", await repeated.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        foreach (var state in new[] { "missing", "removed_from_source" })
        {
            using var restore = new HttpRequestMessage(HttpMethod.Post, "/v1/documents/" + ids[state] + "/restore");
            restore.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
            using var restored = await session.SendAsync(restore);
            Assert.DoesNotContain("plaintext body", await restored.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        // A metadata read still reports the lifecycle state needed for restore.
        var metadata = (await reader.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + ids["deleted"]))!;
        Assert.Equal("deleted", metadata.Status);
        Assert.Null(metadata.Text);
        // An additive tag edit is not a way around the boundary: unavailable documents are not editable.
        using var tags = new HttpRequestMessage(HttpMethod.Patch, "/v1/documents/" + ids["deleted"]) { Content = JsonContent.Create(new { tags = new[] { "secret-tag", "more" } }) };
        tags.Headers.TryAddWithoutValidation("If-Match", "\"2\"");
        using (var edited = await writer.SendAsync(tags))
        {
            Assert.Equal(HttpStatusCode.NotFound, edited.StatusCode);
            Assert.DoesNotContain("plaintext body", await edited.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        var live = (await reader.GetFromJsonAsync<DocumentRecord>("/documents/" + ids["indexed"] + "?include=text,chunks"))!;
        Assert.Contains("private indexed plaintext body", live.Text!, StringComparison.Ordinal);
        Assert.NotEmpty(live.Chunks!);
        using var liveTags = new HttpRequestMessage(HttpMethod.Patch, "/v1/documents/" + ids["indexed"]) { Content = JsonContent.Create(new { tags = new[] { "secret-tag", "more" } }) };
        liveTags.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using (var edited = await writer.SendAsync(liveTags)) Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
    }

    private static async Task<HttpClient> ClientAsync(LaneDWebFactory factory, params Scope[] scopes)
    {
        var repository = factory.Services.GetRequiredService<IAuthRepository>();
        var created = factory.Services.GetRequiredService<CredentialFactory>().CreateApiKey("ingest-test", scopes.ToHashSet(), await repository.GetEpochAsync());
        await repository.AddAsync(created.Record);
        var client = factory.CreatePrivateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", created.Plaintext);
        return client;
    }
}
