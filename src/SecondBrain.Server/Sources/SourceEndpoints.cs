using SecondBrain.Core.Problems;
using SecondBrain.Core.Authorization;

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
