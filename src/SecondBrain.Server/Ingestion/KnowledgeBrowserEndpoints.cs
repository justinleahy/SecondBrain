using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Limits;
using SecondBrain.Core.Retrieval;
using SecondBrain.Core.Sources;
using SecondBrain.Server.Chat;

namespace SecondBrain.Server.Ingestion;

public sealed record TimelineDay(string Day, IReadOnlyList<DocumentRecord> Documents);
public sealed record TimelinePage(string TimeZone, string FromDay, string ToDay, IReadOnlyList<TimelineDay> Days,
    [property: System.Text.Json.Serialization.JsonPropertyName("next_cursor")] string? NextCursor, object? Coverage);

public static class KnowledgeBrowserEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        foreach (var prefix in new[] { "", "/v1" })
        {
            endpoints.MapGet(prefix + "/browse/jobs", JobsAsync).WithMetadata(new EndpointPolicy("jobs.read"))
                .Produces<CatalogPage<CatalogJob>>().ProducesProblem(400);
            endpoints.MapGet(prefix + "/browse/source-counts", SourceCountsAsync).WithMetadata(new EndpointPolicy("sources.list"))
                .Produces<IReadOnlyList<SourceDocumentCounts>>();
            endpoints.MapGet(prefix + "/documents/{id}/metadata", DetailsAsync).WithMetadata(new EndpointPolicy("catalog.read"))
                .Produces<DocumentDetails>().ProducesProblem(404).ProducesProblem(412);
            endpoints.MapGet(prefix + "/documents/{id}/conflicts/{mutation}", ConflictFileAsync).WithMetadata(new EndpointPolicy("catalog.read"))
                .Produces(200, contentType: "application/octet-stream").ProducesProblem(404);
            endpoints.MapGet(prefix + "/documents/{id}/revisions", RevisionsAsync).WithMetadata(new EndpointPolicy("catalog.read"))
                .Produces<CatalogPage<DocumentRevisionEntry>>().ProducesProblem(404);
            endpoints.MapGet(prefix + "/browse/complete", CompleteAsync).WithMetadata(new EndpointPolicy("catalog.complete"), new AdmissionPolicy(AdmissionResource.Search))
                .Produces<IReadOnlyList<CatalogCompletion>>().ProducesProblem(400);
            endpoints.MapPost(prefix + "/search/facets", FacetsAsync).WithMetadata(new EndpointPolicy("search.facets"), new AdmissionPolicy(AdmissionResource.Search))
                .Accepts<FacetRequest>("application/json").Produces<IReadOnlyList<TypeCount>>().ProducesProblem(400);
            endpoints.MapGet(prefix + "/types", TypesAsync).WithMetadata(new EndpointPolicy("catalog.read"))
                .Produces<IReadOnlyList<TypeCount>>();
            endpoints.MapGet(prefix + "/browse/sources", SourcesAsync).WithMetadata(new EndpointPolicy("catalog.read"))
                .Produces<CatalogPage<CatalogSource>>();
            endpoints.MapGet(prefix + "/timeline", TimelineAsync)
                .WithMetadata(new EndpointPolicy("catalog.read"), new AdmissionPolicy(AdmissionResource.Search)).Produces<TimelinePage>().ProducesProblem(400);
            endpoints.MapGet(prefix + "/trash", TrashAsync).WithMetadata(new EndpointPolicy("catalog.read"))
                .Produces<CatalogPage<TrashedDocument>>().ProducesProblem(400);
            endpoints.MapGet(prefix + "/suppressions", SuppressionsAsync).WithMetadata(new EndpointPolicy("suppressions.read"))
                .Produces<CatalogPage<SuppressionRecord>>().ProducesProblem(400);
            endpoints.MapDelete(prefix + "/suppressions/{id}", LiftAsync)
                .WithMetadata(new EndpointPolicy("suppressions.lift", SessionOnly: true)).Produces(204).ProducesProblem(404);
            endpoints.MapGet(prefix + "/tags", TagsAsync).WithMetadata(new EndpointPolicy("catalog.read"))
                .Produces<TagPage>().ProducesProblem(400);
            endpoints.MapGet(prefix + "/documents/{id}/related", RelatedAsync).WithMetadata(new EndpointPolicy("catalog.read"))
                .Produces<IReadOnlyList<DocumentRecord>>().ProducesProblem(404);
        }
    }

    private static Task<IResult> ConflictFileAsync(string id, string mutation, HttpContext context, IDocumentCatalog catalog, IManagedDocumentFiles files, IAuthRepository auth, CancellationToken token) => GuardAsync(context, async () =>
    {
        var file = await catalog.ConflictFileAsync(id, mutation, await KnowledgeEndpoints.AuthorityAsync(context, auth, token), token);
        if (file is null) return Results.NotFound();
        try
        {
            var stream = await files.OpenReadAsync(file.Path, token);
            try
            {
                var current = await catalog.ConflictFileAsync(id, mutation, await KnowledgeEndpoints.AuthorityAsync(context, auth, token), token);
                if (current?.Path != file.Path) { await stream.DisposeAsync(); return Results.NotFound(); }
            }
            catch { await stream.DisposeAsync(); throw; }
            context.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
            return Results.File(stream, "application/octet-stream", file.Name);
        }
        catch (IOException) { return Results.NotFound(); }
    });

    private static Task<IResult> JobsAsync(HttpContext context, IDocumentCatalog catalog, IAuthRepository auth, string? status, string? source, int? limit, string? cursor, CancellationToken token) =>
        GuardAsync(context, async () => Results.Ok(await catalog.JobsAsync(await KnowledgeEndpoints.AuthorityAsync(context, auth, token), string.IsNullOrEmpty(status) ? null : status, string.IsNullOrEmpty(source) ? null : source, limit ?? 50, cursor, token)));
    private static Task<IResult> SourceCountsAsync(HttpContext context, IDocumentCatalog catalog, IAuthRepository auth, CancellationToken token) =>
        GuardAsync(context, async () => Results.Ok(await catalog.SourceCountsAsync(await KnowledgeEndpoints.AuthorityAsync(context, auth, token), token)));

    private static Task<IResult> DetailsAsync(string id, long? revision, HttpContext context, IDocumentCatalog catalog, IDocumentRepository documents, IAuthRepository auth, CancellationToken token) => GuardAsync(context, async () =>
    {
        var result = await catalog.DetailsAsync(id, await KnowledgeEndpoints.AuthorityAsync(context, auth, token), revision, token);
        var extracted = await documents.CachedExtractionAsync(id, token);
        var current = await documents.GetAsync(id, includeText: true, cancellationToken: token);
        if (current is null || DocumentStatusNames.IsUnavailable(current.Status)) return Results.NotFound();
        if (current.Revision != result.Revision) return Results.Problem(statusCode: 412, title: "revision-mismatch");
        if (extracted?.PhysicalFormat == "pdf" && extracted.LosslessText == current.Text && extracted.Pages is { Count: > 0 } pages)
            result = result with { Pages = pages.Select(page => new DocumentPage(page.Number, page.Start, page.Length)).ToArray() };
        await KnowledgeEndpoints.AuthorityAsync(context, auth, token);
        return Results.Ok(result);
    });
    private static Task<IResult> RevisionsAsync(string id, int? limit, string? cursor, HttpContext context, IDocumentCatalog catalog, IAuthRepository auth, CancellationToken token) =>
        GuardAsync(context, async () => Results.Ok(await catalog.RevisionsAsync(id, await KnowledgeEndpoints.AuthorityAsync(context, auth, token), limit ?? 50, cursor, token)));

    private static Task<IResult> CompleteAsync(HttpContext context, IDocumentCatalog catalog, IAuthRepository auth, string kind, string? q, int? limit, CancellationToken token) => GuardAsync(context, async () =>
    {
        var result = await catalog.CompleteAsync(kind, q ?? "", await KnowledgeEndpoints.AuthorityAsync(context, auth, token), limit ?? 20, token);
        await KnowledgeEndpoints.AuthorityAsync(context, auth, token);
        return Results.Ok(result);
    });

    private static Task<IResult> FacetsAsync(HttpContext context, IRetrievalRepository retrieval, IAuthRepository auth, CancellationToken token) => GuardAsync(context, async () =>
    {
        var request = await KnowledgeEndpoints.ReadJsonAsync<FacetRequest>(context.Request.Body, 65536, token);
        await KnowledgeEndpoints.AuthorityAsync(context, auth, token);
        var result = await retrieval.TypeCountsAsync(request, token);
        await KnowledgeEndpoints.AuthorityAsync(context, auth, token);
        return Results.Ok(result);
    });
    private static Task<IResult> TypesAsync(HttpContext context, IRetrievalRepository retrieval, IAuthRepository auth, CancellationToken token) => GuardAsync(context, async () =>
    {
        await KnowledgeEndpoints.AuthorityAsync(context, auth, token);
        var result = await retrieval.TypeCountsAsync(new(), token);
        await KnowledgeEndpoints.AuthorityAsync(context, auth, token);
        return Results.Ok(result);
    });
    private static Task<IResult> SourcesAsync(HttpContext context, IDocumentCatalog catalog, IAuthRepository auth, int? limit, string? cursor, CancellationToken token) =>
        GuardAsync(context, async () => Results.Ok(await catalog.SourcesAsync(await KnowledgeEndpoints.AuthorityAsync(context, auth, token), limit ?? 200, cursor, token)));

    private static Task<IResult> TimelineAsync(HttpContext context, IRetrievalRepository retrieval, IAuthRepository auth,
        IOptionsMonitor<SecondBrainOptions> options, TimeProvider clock, string? from, string? to, string? type, string? people,
        int? limit, string? cursor, CancellationToken token) => GuardAsync(context, async () =>
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.CurrentValue.Time.Zone);
        var window = TimelineWindow.Resolve(from, to, clock.GetUtcNow(), zone);
        var persons = people?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(value => new PersonFilter(value)).ToArray();
        if (people?.Length > 8192 || persons?.Length > 100) throw new ArgumentException("Too many people filters.");
        var page = await retrieval.QueryAsync(new QueryRequest(Type: type, People: persons,
            Occurred: new(window.FromInstant, window.ToInstant), Sort: "occurred_at", Limit: limit ?? 50, Cursor: cursor),
            await KnowledgeEndpoints.AuthorityAsync(context, auth, token), token);
        var days = page.Records.Where(document => document.OccurredAt is not null)
            .GroupBy(document => TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(document.OccurredAt!, CultureInfo.InvariantCulture), zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Select(group => new TimelineDay(group.Key, group.ToArray())).ToArray();
        await KnowledgeEndpoints.AuthorityAsync(context, auth, token);
        return Results.Ok(new TimelinePage(zone.Id, window.FromDay, window.ToDay, days, page.NextCursor, page.Coverage));
    });

    private static Task<IResult> TrashAsync(HttpContext context, IDocumentCatalog catalog, IAuthRepository auth, int? limit, string? cursor, CancellationToken token) =>
        GuardAsync(context, async () => Results.Ok(await catalog.TrashAsync(await KnowledgeEndpoints.AuthorityAsync(context, auth, token), limit ?? 50, cursor, token)));

    private static Task<IResult> SuppressionsAsync(HttpContext context, IDocumentCatalog catalog, IAuthRepository auth, int? limit, string? cursor, CancellationToken token) =>
        GuardAsync(context, async () => Results.Ok(await catalog.SuppressionsAsync(await KnowledgeEndpoints.AuthorityAsync(context, auth, token), limit ?? 50, cursor, token)));

    private static Task<IResult> LiftAsync(string id, HttpContext context, IDocumentCatalog catalog, IAuthRepository auth, CancellationToken token) =>
        GuardAsync(context, async () => await catalog.LiftSuppressionAsync(id, await KnowledgeEndpoints.AuthorityAsync(context, auth, token), token)
            ? Results.NoContent() : Results.NotFound());

    private static Task<IResult> TagsAsync(HttpContext context, IRetrievalRepository retrieval, IAuthRepository auth, int? limit, string? cursor, CancellationToken token) =>
        GuardAsync(context, async () => Results.Ok(await retrieval.TagsAsync(await KnowledgeEndpoints.AuthorityAsync(context, auth, token), limit ?? 100, cursor, token)));

    private static Task<IResult> RelatedAsync(string id, HttpContext context, IDocumentRepository documents, IRetrievalRepository retrieval, IAuthRepository auth, int? limit, CancellationToken token) =>
        GuardAsync(context, async () =>
        {
            var authority = await KnowledgeEndpoints.AuthorityAsync(context, auth, token);
            var document = await documents.GetAsync(id, cancellationToken: token);
            if (document is null || DocumentStatusNames.IsUnavailable(document.Status)) return Results.NotFound();
            var result = await retrieval.RelatedAsync(id, authority, limit ?? 8, token);
            await KnowledgeEndpoints.AuthorityAsync(context, auth, token);
            return Results.Ok(result);
        });

    private static async Task<IResult> GuardAsync(HttpContext context, Func<Task<IResult>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (AuthorityChangedException) { return Results.Problem(statusCode: 409, title: "Your session changed. Sign in again."); }
        catch (ChatNotFoundException) { return Results.NotFound(); }
        catch (IngestionConflictException exception) { return Results.Problem(statusCode: exception.Code == "not-found" ? 404 : exception.Code == "revision-mismatch" ? 412 : 409, title: exception.Code); }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: exception.Message); }
        catch (JsonException) { return Results.Problem(statusCode: 400, title: "Invalid request."); }
        catch (InvalidDataException) { return Results.Problem(statusCode: 413, title: "Request body is too large."); }
    }
}
