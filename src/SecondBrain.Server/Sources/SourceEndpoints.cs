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
                .Produces<SourceResponse>(StatusCodes.Status201Created)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status403Forbidden);
            endpoints.MapPost(prefix + "/sources/{id}/scan", ScanAsync).WithMetadata(new EndpointPolicy("sources.scan")).Produces<object>().ProducesProblem(404);
            endpoints.MapPatch(prefix + "/sources/{id}", UpdateAsync).WithMetadata(new EndpointPolicy("sources.mutate", SessionOnly: true, StepUp: true)).Produces(StatusCodes.Status204NoContent).ProducesProblem(400).ProducesProblem(404);
            endpoints.MapDelete(prefix + "/sources/{id}", RemoveAsync).WithMetadata(new EndpointPolicy("sources.mutate", SessionOnly: true, StepUp: true)).Produces(StatusCodes.Status204NoContent).ProducesProblem(404);
            endpoints.MapGet(prefix + "/sources", ListAsync)
                .WithMetadata(new EndpointPolicy("sources.list"))
                .Produces<IReadOnlyList<SourceResponse>>();
            endpoints.MapGet(prefix + "/sources/{id}", GetAsync).WithMetadata(new EndpointPolicy("sources.list")).Produces<SourceResponse>().ProducesProblem(404);
        }
    }

    private static async Task<IResult> CreateAsync(CreateSourceRequest request, HttpContext context, ISourcePathValidator paths, ISourceRepository repository,
        CalendarSubscriptionService calendars, CalendarSubscriptionOrchestrator polling, IAuthRepository auth, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (request.Kind == "calendar_subscription")
        {
            try
            {
                var calendar = await calendars.CreateAsync(request.Name ?? "Shared calendar", request.Url ?? "", request.PollIntervalSeconds ?? 3600, request.Enabled ?? true,
                    await AuthorityAsync(context, auth, cancellationToken), cancellationToken);
                polling.Notify();
                return Results.Created("/v1/sources/" + calendar.Id, SourceResponse.From(calendar));
            }
            catch (CalendarSubscriptionException error) { return CalendarProblem(error); }
            catch (IngestionConflictException error) { return Results.Problem(statusCode: 409, title: error.Code); }
        }
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
            new(canonical, request.Include, request.Exclude, request.Recursive, request.Mode, request.Enrich, request.DefaultType, request.TypeMap, request.Watch, request.PollIntervalSeconds ?? 600),
            "active", null, null, timeProvider.GetUtcNow());
        await repository.AddAsync(source, cancellationToken);
        return Results.Created("/v1/sources/" + source.Id, SourceResponse.From(source));
    }

    private static async Task<IResult> ListAsync(ISourceRepository repository, ICalendarSubscriptionRepository calendars, CancellationToken cancellationToken) =>
        Results.Ok((await repository.ListAsync(cancellationToken)).Select(SourceResponse.From).Concat((await calendars.ListAsync(cancellationToken)).Select(SourceResponse.From)).ToArray());
    private static async Task<IResult> GetAsync(string id, ISourceRepository repository, ICalendarSubscriptionRepository calendars, CancellationToken token)
    {
        if (await calendars.GetAsync(id, token) is { } calendar) return Results.Ok(SourceResponse.From(calendar));
        var source = (await repository.ListAsync(token)).FirstOrDefault(item => item.Id == id);
        return source is null ? Results.NotFound() : Results.Ok(SourceResponse.From(source));
    }

    private static async Task<IResult> ScanAsync(string id, string? deep, HttpContext context, ISourceRepository sources, FolderProcessingOrchestrator scans,
        ICalendarSubscriptionRepository calendars, CalendarSubscriptionOrchestrator polling, IAuthRepository auth, CancellationToken token)
    {
        if (deep is not (null or "0" or "1" or "false" or "true")) return Results.BadRequest();
        if (await calendars.GetAsync(id, token) is not null)
        {
            try { await calendars.QueueAsync(id, deep is "1" or "true", await AuthorityAsync(context, auth, token), token); polling.Notify(); return Results.Ok(new { source_id = id, queued = true }); }
            catch (CalendarSubscriptionException error) { return CalendarProblem(error); }
            catch (IngestionConflictException error) { return Results.Problem(statusCode: 409, title: error.Code); }
        }
        var source = (await sources.ListAsync(token)).FirstOrDefault(item => item.Id == id);
        if (source is null) return Results.NotFound();
        try { return Results.Ok(new { admitted = await scans.ScanAsync(source, deep is "1" or "true", token) }); }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException or InvalidDataException or IngestionConflictException)
        { return Results.Problem(statusCode: 503, title: "source-scan-failed", detail: "The scan could not complete. The source retains its last scan error and can be scanned again."); }
    }

    private static async Task<IResult> UpdateAsync(string id, SourceStatusRequest request, HttpContext context, IFolderDocumentRepository repository,
        ICalendarSubscriptionRepository calendars, CalendarSubscriptionOrchestrator polling, IAuthRepository auth, CancellationToken token)
    {
        if (request.Extra?.Count > 0) return Results.BadRequest();
        if (await calendars.GetAsync(id, token) is not null)
        {
            if (request.Status is not (null or "active" or "disabled") || request.Status is not null && request.Enabled is not null) return Results.BadRequest();
            var enabled = request.Enabled ?? (request.Status is null ? null : request.Status == "active");
            if (enabled is null && request.Name is null && request.PollIntervalSeconds is null) return Results.BadRequest();
            try { await calendars.UpdateAsync(id, request.Name, request.PollIntervalSeconds, enabled, await AuthorityAsync(context, auth, token), token); polling.Notify(); return Results.NoContent(); }
            catch (CalendarSubscriptionException error) { return CalendarProblem(error); }
            catch (IngestionConflictException error) { return Results.Problem(statusCode: error.Code == "not-found" ? 404 : 409, title: error.Code); }
        }
        return request.Status is "active" or "disabled" ? await ChangeStatusAsync(id, request.Status, context, repository, auth, token) : Results.BadRequest();
    }
    private static async Task<IResult> RemoveAsync(string id, HttpContext context, IFolderDocumentRepository repository, ICalendarSubscriptionRepository calendars, IAuthRepository auth, CancellationToken token)
    {
        var requested = context.Request.Query["purge"].ToString();
        if (requested is not ("" or "0" or "1")) return Results.BadRequest();
        var purge = requested == "1";
        if (await calendars.GetAsync(id, token, includeRemoved: true) is null)
        {
            if (!purge) return await ChangeStatusAsync(id, "removed", context, repository, auth, token);
            try { await repository.PurgeAsync(id, await AuthorityAsync(context, auth, token), token); return Results.NoContent(); }
            catch (IngestionConflictException error) { return Results.Problem(statusCode: error.Code == "not-found" ? 404 : 409, title: error.Code); }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
            { return Results.Problem(statusCode: 503, title: "Folder purge remains pending. The server will retry its saved request."); }
        }
        try
        {
            var actor = await AuthorityAsync(context, auth, token);
            if (purge) await calendars.PurgeAsync(id, actor, token);
            else await calendars.RemoveAsync(id, actor, token);
            return Results.NoContent();
        }
        catch (IngestionConflictException error) { return Results.Problem(statusCode: error.Code == "not-found" ? 404 : 409, title: error.Code); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        { return Results.Problem(statusCode: 503, title: purge ? "Calendar purge remains pending. The server will retry its saved request." : "Calendar removal could not finish. Retry the request."); }
    }
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
        request.Kind == "folder" && request.Url is null && (request.Name is null || (!string.IsNullOrWhiteSpace(request.Name) && request.Name.Length <= 200)) &&
        request.Mode is "index" or "import" &&
        request.Watch is "events" or "poll" &&
        (request.PollIntervalSeconds ?? 600) is >= 1 and <= 86_400 &&
        (request.DefaultType is null || (!string.IsNullOrWhiteSpace(request.DefaultType) && request.DefaultType.Length <= 64)) &&
        request.Include is not null && request.Exclude is not null && request.TypeMap is not null &&
        request.Include.Count <= 128 && request.Exclude.Count <= 128 && request.TypeMap.Count <= 128 &&
        request.Include.Concat(request.Exclude).All(glob => !string.IsNullOrWhiteSpace(glob) && glob.Length <= 1024) &&
        request.TypeMap.All(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Key.Length <= 1024 && !string.IsNullOrWhiteSpace(entry.Value) && entry.Value.Length <= 64);

    private static IResult CalendarProblem(CalendarSubscriptionException error) => Results.Problem(statusCode: error.Code == "calendar_source_paused" ? 409 : 400,
        title: error.Code switch
        {
            "calendar_url_invalid" => "Enter a valid HTTPS or webcal calendar link without credentials or a fragment.",
            "calendar_origin_not_allowed" => "This calendar's host must be allowed in the server configuration first.",
            "calendar_settings_invalid" => "Use a name of 1–200 characters and a refresh interval of at least five minutes.",
            "calendar_source_paused" => "Resume the calendar before requesting a refresh.",
            "calendar_container_unavailable" => "Restore the calendar container before resuming this subscription.",
            _ => "The calendar request could not be completed."
        }, extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    private static async Task<IngestionAuthority> AuthorityAsync(HttpContext context, IAuthRepository auth, CancellationToken token)
    {
        var identity = (AuthenticatedCredential)context.Items[typeof(AuthenticatedCredential)]!;
        if (!await auth.IsCurrentAsync(identity.Id, identity.Generation, identity.AccountEpoch, token)) throw new AuthorityChangedException();
        var credential = await auth.FindAsync(identity.Id, token) ?? throw new AuthorityChangedException();
        return new(identity.Id, identity.Generation, identity.AccountEpoch, credential.Name, identity.Kind);
    }
}

public sealed record SourceStatusRequest(string? Status = null, string? Name = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("poll_interval_s")] int? PollIntervalSeconds = null, bool? Enabled = null)
{
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; init; }
    public override string ToString() => "Source update";
}
