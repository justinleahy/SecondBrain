using System.Globalization;
using System.Text.Json;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Limits;
using SecondBrain.Server.Http;
using SecondBrain.Server.Ingestion;

namespace SecondBrain.Server.Chat;

public static class ChatEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        foreach (var prefix in new[] { "", "/v1" })
        {
            endpoints.MapPost(prefix + "/chat", StartAsync).WithMetadata(new EndpointPolicy("chat.start"))
                .Accepts<ChatRequest>("application/json").Produces<ChatStart>(202);
            endpoints.MapPost(prefix + "/ask", AskAsync).WithMetadata(new EndpointPolicy("chat.start"))
                .Accepts<ChatRequest>("application/json").Produces<ChatStart>(202);
            endpoints.MapGet(prefix + "/conversations", ListAsync).WithMetadata(new EndpointPolicy("chat.read"))
                .Produces<IReadOnlyList<ChatConversation>>();
            endpoints.MapGet(prefix + "/conversations/{id}", ConversationAsync).WithMetadata(new EndpointPolicy("chat.read"))
                .Produces<IReadOnlyList<ChatTurn>>();
            endpoints.MapGet(prefix + "/turns/{id}", TurnAsync).WithMetadata(new EndpointPolicy("chat.read")).Produces<ChatTurn>();
            endpoints.MapGet(prefix + "/turns/{id}/manifest", ManifestAsync).WithMetadata(new EndpointPolicy("chat.read")).Produces<ChatEgressManifest>();
            endpoints.MapGet(prefix + "/turns/{id}/events", EventsAsync)
                .WithMetadata(new EndpointPolicy("chat.read"), new AdmissionPolicy(AdmissionResource.Stream))
                .Produces(200, contentType: "text/event-stream");
            endpoints.MapGet(prefix + "/pending", PendingAsync).WithMetadata(new EndpointPolicy("approvals.read"))
                .Produces<IReadOnlyList<ChatProposal>>();
            endpoints.MapPost(prefix + "/pending/{id}/approve", ApproveAsync).WithMetadata(new EndpointPolicy("approvals.resolve", SessionOnly: true)).Produces<ChatProposal>();
            endpoints.MapPost(prefix + "/pending/{id}/discard", DiscardAsync).WithMetadata(new EndpointPolicy("approvals.resolve", SessionOnly: true)).Produces<ChatProposal>();
            endpoints.MapPost(prefix + "/pending/{id}/edit", EditAsync).WithMetadata(new EndpointPolicy("approvals.resolve", SessionOnly: true))
                .Accepts<JsonElement>("application/json").Produces<ChatProposal>();
        }
    }

    private static Task<IResult> StartAsync(HttpContext context, ChatService service, CancellationToken token) => StartCoreAsync(context, service, false, token);
    private static Task<IResult> AskAsync(HttpContext context, ChatService service, CancellationToken token) => StartCoreAsync(context, service, true, token);
    private static Task<IResult> StartCoreAsync(HttpContext context, ChatService service, bool ask, CancellationToken token) => GuardAsync(async () =>
    {
        var request = await KnowledgeEndpoints.ReadJsonAsync<ChatRequest>(context.Request.Body, 65_536, token);
        if (context.Request.Headers["Idempotency-Key"].Count > 1) throw new ArgumentException("Use one idempotency key.");
        if (context.Request.Headers["Idempotency-Key"].FirstOrDefault() is { } key)
        {
            if (request.RequestId is not null && request.RequestId != key) throw new ArgumentException("Conflicting request IDs.");
            request = request with { RequestId = key };
        }
        if (ask) request = request with { Mode = "ask" };
        return Results.Json(await service.StartAsync(request, Actor(context), token), statusCode: 202);
    });
    private static Task<IResult> ListAsync(HttpContext context, IChatRepository repository, CancellationToken token) =>
        GuardAsync(async () => Results.Ok(await repository.ListAsync(Actor(context), token)));
    private static Task<IResult> ConversationAsync(string id, HttpContext context, IChatRepository repository, CancellationToken token) =>
        GuardAsync(async () => Results.Ok(await repository.GetConversationAsync(id, Actor(context), token)));
    private static Task<IResult> TurnAsync(string id, HttpContext context, IChatRepository repository, CancellationToken token) =>
        GuardAsync(async () => Results.Ok(await repository.GetTurnAsync(id, Actor(context), token)));
    private static Task<IResult> PendingAsync(string? conversationId, HttpContext context, IChatProposalRepository repository, CancellationToken token) =>
        GuardAsync(async () => Results.Ok(await repository.ListAsync(conversationId, Actor(context), token)));
    private static Task<IResult> ManifestAsync(string id, HttpContext context, IChatExecutionRepository repository, CancellationToken token) =>
        GuardAsync(async () => Results.Ok(await repository.ManifestAsync(id, Actor(context), token)));
    private static Task<IResult> ApproveAsync(string id, HttpContext context, IChatProposalRepository repository, CancellationToken token) =>
        GuardAsync(async () => Results.Ok(await repository.ApproveAsync(id, Actor(context), cancellationToken: token)));
    private static Task<IResult> DiscardAsync(string id, HttpContext context, IChatProposalRepository repository, CancellationToken token) =>
        GuardAsync(async () => Results.Ok(await repository.DiscardAsync(id, Actor(context), token)));
    private static Task<IResult> EditAsync(string id, HttpContext context, IChatProposalRepository repository, CancellationToken token) =>
        GuardAsync(async () => Results.Ok(await repository.EditAsync(id,
            await KnowledgeEndpoints.ReadJsonAsync<JsonElement>(context.Request.Body, 65_536, token), Actor(context), token)));

    private static async Task EventsAsync(string id, HttpContext context, IChatRepository repository, CancellationToken token)
    {
        var cursor = context.Request.Headers["Last-Event-ID"].FirstOrDefault() ?? context.Request.Query["after"].FirstOrDefault() ?? "0";
        if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var after))
        { await ProblemResponses.WriteAsync(context, 400, ProblemResponses.TypeForStatus(400), "Invalid event cursor."); return; }
        var actor = Actor(context);
        var initial = await GuardAsync(async () => { await repository.GetTurnAsync(id, actor, token); return Results.Ok(); });
        if (initial is IStatusCodeHttpResult { StatusCode: >= 400 }) { await initial.ExecuteAsync(context); return; }
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        var empty = 0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
            do
            {
                var events = await repository.EventsAsync(id, after, actor, token);
                foreach (var item in events)
                {
                    await context.Response.WriteAsync($"id: {item.Sequence.ToString(CultureInfo.InvariantCulture)}\nevent: {item.Kind}\ndata: {JsonSerializer.Serialize(item, Json)}\n\n", token);
                    after = item.Sequence;
                }
                if (events.Count == 0 && ++empty % 30 == 0) await context.Response.WriteAsync(": keep-alive\n\n", token);
                await context.Response.Body.FlushAsync(token);
                if (events.Count < 128 && (await repository.GetTurnAsync(id, actor, token)).Status != "running") return;
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (AuthorityChangedException) { /* Close replay immediately when authority changes. The host owns the turn. */ }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static AuthenticatedCredential Actor(HttpContext context) => (AuthenticatedCredential)context.Items[typeof(AuthenticatedCredential)]!;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ChatNotFoundException) { return Problem(404, "Conversation, turn, or proposal not found."); }
        catch (AuthorityChangedException) { return Problem(409, "Credential authority changed; authenticate again."); }
        catch (ChatLimitException exception) { return Problem(429, exception.Message); }
        catch (ChatConflictException exception) { return Problem(409, exception.Message); }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return Problem(400, "Invalid chat request."); }
        catch (InvalidDataException) { return Problem(413, "Chat request is too large."); }
    }
    private static IResult Problem(int status, string title) => Results.Problem(statusCode: status, type: ProblemResponses.TypeForStatus(status), title: title);
}
