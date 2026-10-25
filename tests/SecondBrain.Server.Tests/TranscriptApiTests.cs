using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Extraction.Contracts;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class TranscriptApiTests
{
    [Fact]
    public async Task NightlyTypedCaptureRetainsParticipantsAndStartTime()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await ClientAsync(factory);
        using var response = await client.PostAsJsonAsync("/v1/documents", new CaptureRequest("Ada: Pricing needs review.", "Meeting", "transcript",
            JsonSerializer.SerializeToElement(new { participants = new[] { "ada@example.com" }, started_at = "2026-10-24T09:00:00-04:00", duration_ms = 10000 })));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var captured = (await response.Content.ReadFromJsonAsync<CaptureResult>())!.Document;
        using var query = await client.PostAsJsonAsync("/v1/query", new QueryRequest(Type: "transcript", People: [new("ada@example.com", "participant")], Occurred: new("2026-10-24T12:00:00Z", "2026-10-24T14:00:00Z")));
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        Assert.Equal(captured.Id, Assert.Single((await query.Content.ReadFromJsonAsync<QueryPage>())!.Records).Id);
    }

    [Fact]
    public async Task UploadedTranscriptExposesSegmentsAndSpeakerSearch()
    {
        await using var factory = new LaneDWebFactory(configureServices: services =>
        {
            services.RemoveAll<IExtractorClient>(); services.AddSingleton<IExtractorClient>(new Extractor());
        });
        using var client = await ClientAsync(factory);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("synthetic vtt bytes")), "file", "meeting.vtt");
        using var response = await client.PostAsync("/v1/documents", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var captured = (await response.Content.ReadFromJsonAsync<CaptureResult>())!.Document;
        DocumentRecord? document = null;
        for (var i = 0; i < 100; i++)
        {
            document = await client.GetFromJsonAsync<DocumentRecord>("/v1/documents/" + captured.Id + "?include=segments,chunks,text");
            if (document?.Segments?.Count > 0) break;
            await Task.Delay(50);
        }
        Assert.NotNull(document);
        Assert.Equal("transcript", document.Type);
        Assert.Equal(new(0, "Ada", 1000, 2000, 0, 19), Assert.Single(document.Segments!));
        // Extraction metadata commits before its asynchronous searchable projection.
        // Wait on the actual keyword result rather than treating segments as a publication signal.
        SearchResponse? results = null;
        for (var i = 0; i < 100; i++)
        {
            using var search = await client.PostAsJsonAsync("/v1/search", new SearchRequest("Pricing", "keyword", new(Type: "transcript", People: [new("Ada", "speaker")])));
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
            results = await search.Content.ReadFromJsonAsync<SearchResponse>();
            if (results!.Results.Count > 0) break;
            await Task.Delay(50);
        }
        var hit = Assert.Single(results!.Results);
        Assert.Equal(new(0, 0, "Ada", 1000, 2000), hit.Transcript);
    }

    private static async Task<HttpClient> ClientAsync(LaneDWebFactory factory)
    {
        var auth = factory.Services.GetRequiredService<IAuthRepository>();
        var credential = factory.Services.GetRequiredService<CredentialFactory>().CreateApiKey("transcript-script", new HashSet<Scope> { Scope.Read, Scope.Write }, await auth.GetEpochAsync());
        await auth.AddAsync(credential.Record);
        var client = factory.CreatePrivateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", credential.Plaintext);
        return client;
    }
    private sealed class Extractor : IExtractorClient
    {
        public ValueTask<ExtractionResult> ExtractAsync(Stream stagedInput, string formatHint, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtractionResult("extracted", "vtt", "text/vtt", "Pricing needs work.", Segments: [new(0, "Ada", 1000, 2000, 0, 19)]));
    }
}
