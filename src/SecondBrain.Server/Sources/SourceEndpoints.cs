using SecondBrain.Core.Problems;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Sources;

namespace SecondBrain.Server.Sources;

public static class SourceEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        foreach (var prefix in new[] { "", "/v1" })
        {
            endpoints.MapPost(prefix + "/sources", CreateAsync)
                .WithMetadata(new EndpointPolicy("sources.create", StepUp: true))
                .Produces<SourceRecord>(StatusCodes.Status201Created)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status403Forbidden);
            endpoints.MapPost(prefix + "/sources/{id}/scan", ScanAsync).WithMetadata(new EndpointPolicy("sources.scan")).Produces<object>().ProducesProblem(404);
            endpoints.MapPatch(prefix + "/sources/{id}", UpdateAsync).WithMetadata(new EndpointPolicy("sources.mutate", SessionOnly: true, StepUp: true)).Produces(StatusCodes.Status204NoContent).ProducesProblem(400).ProducesProblem(404);
            endpoints.MapDelete(prefix + "/sources/{id}", RemoveAsync).WithMetadata(new EndpointPolicy("sources.mutate", SessionOnly: true, StepUp: true)).Produces(StatusCodes.Status204NoContent).ProducesProblem(404);
            endpoints.MapGet(prefix + "/sources", ListAsync)
                .WithMetadata(new EndpointPolicy("sources.list"))
                .Produces<IReadOnlyList<SourceRecord>>();
        }
    }

    private static async Task<IResult> CreateAsync(CreateSourceRequest request, ISourcePathValidator paths, ISourceRepository repository, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (!ValidRequest(request))
        {
            return Results.Problem(statusCode: 400, type: ProblemTypes.InvalidRequest, title: "Invalid source configuration", detail: "Source names, globs, type mappings, mode, and watch settings must satisfy the source schema.");
        }

        var validation = paths.Validate(request.Path);
        if (!validation.Accepted)
        {
            return Results.Problem(statusCode: 400, type: ProblemTypes.SourcePathRejected, title: "Source path rejected", detail: validation.Reason);
        }

        var canonical = validation.CanonicalPath!;
        var source = new SourceRecord(
            Ulid.NewUlid().ToString(), "folder", request.Name ?? new DirectoryInfo(canonical).Name,
            new(canonical, request.Include, request.Exclude, request.Recursive, request.Mode, request.Enrich, request.DefaultType, request.TypeMap, request.Watch, request.PollIntervalSeconds),
            "active", null, null, timeProvider.GetUtcNow());
        await repository.AddAsync(source, cancellationToken);
        return Results.Created("/v1/sources/" + source.Id, source);
    }

    private static async Task<IResult> ListAsync(ISourceRepository repository, CancellationToken cancellationToken) => Results.Ok(await repository.ListAsync(cancellationToken));

    private static async Task<IResult> ScanAsync(string id, string? deep, ISourceRepository sources, FolderProcessingOrchestrator scans, CancellationToken token)
    {
        if (deep is not (null or "0" or "1" or "false" or "true")) return Results.BadRequest();
        var source = (await sources.ListAsync(token)).FirstOrDefault(item => item.Id == id);
        return source is null ? Results.NotFound() : Results.Ok(new { admitted = await scans.ScanAsync(source, deep is "1" or "true", token) });
    }

    private static Task<IResult> UpdateAsync(string id, SourceStatusRequest request, HttpContext context, IFolderDocumentRepository repository, IAuthRepository auth, CancellationToken token) =>
        request.Status is "active" or "disabled" ? ChangeStatusAsync(id, request.Status, context, repository, auth, token) : Task.FromResult<IResult>(Results.BadRequest());
    private static Task<IResult> RemoveAsync(string id, HttpContext context, IFolderDocumentRepository repository, IAuthRepository auth, CancellationToken token) => ChangeStatusAsync(id, "removed", context, repository, auth, token);
    private static async Task<IResult> ChangeStatusAsync(string id, string status, HttpContext context, IFolderDocumentRepository repository, IAuthRepository auth, CancellationToken token)
    {
        var identity = (AuthenticatedCredential)context.Items[typeof(AuthenticatedCredential)]!;
        if (!await auth.IsCurrentAsync(identity.Id, identity.Generation, identity.AccountEpoch, token)) throw new AuthorityChangedException();
        var credential = await auth.FindAsync(identity.Id, token) ?? throw new AuthorityChangedException();
        try
        {
            await repository.SetStatusAsync(id, status, new(identity.Id, identity.Generation, identity.AccountEpoch, credential.Name, identity.Kind), token);
            return Results.NoContent();
        }
        catch (IngestionConflictException ex) { return Results.Problem(statusCode: ex.Code == "not-found" ? 404 : 409, title: ex.Code); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }

    private static bool ValidRequest(CreateSourceRequest request) =>
        (request.Name is null || (!string.IsNullOrWhiteSpace(request.Name) && request.Name.Length <= 200)) &&
        request.Mode is "index" or "import" &&
        request.Watch is "events" or "poll" &&
        request.PollIntervalSeconds is >= 1 and <= 86_400 &&
        (request.DefaultType is null || (!string.IsNullOrWhiteSpace(request.DefaultType) && request.DefaultType.Length <= 64)) &&
        request.Include is not null && request.Exclude is not null && request.TypeMap is not null &&
        request.Include.Count <= 128 && request.Exclude.Count <= 128 && request.TypeMap.Count <= 128 &&
        request.Include.Concat(request.Exclude).All(glob => !string.IsNullOrWhiteSpace(glob) && glob.Length <= 1024) &&
        request.TypeMap.All(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Key.Length <= 1024 && !string.IsNullOrWhiteSpace(entry.Value) && entry.Value.Length <= 64);
}

public sealed record SourceStatusRequest(string Status);
