using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Server.Http;
using SecondBrain.Server.Ingestion;

namespace SecondBrain.Server.Auth;

public sealed record CliPairingBeginRequest(string Name);
public sealed record CliPairingPollRequest(string Id, string PollSecret);
public static class CliPairingEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        foreach (var prefix in new[] { "", "/v1" })
        {
            endpoints.MapPost(prefix + "/auth/cli/start", async (HttpContext context, CliPairingService service,
                Microsoft.Extensions.Options.IOptionsMonitor<SecondBrain.Core.Configuration.SecondBrainOptions> options) => await Guard(async () =>
            {
                context.Response.Headers.CacheControl = "no-store";
                var request = await KnowledgeEndpoints.ReadJsonAsync<CliPairingBeginRequest>(context.Request.Body, 2048, context.RequestAborted);
                return Results.Ok(await service.BeginAsync(request.Name, AuthEndpoints.Source(context), CredentialService.IsSecure(context, options.CurrentValue), context.RequestAborted));
            })).WithMetadata(new BrowserFacingRequest(), new PasswordRequest());
            endpoints.MapPost(prefix + "/auth/cli/poll", async (HttpContext context, CliPairingService service,
                Microsoft.Extensions.Options.IOptionsMonitor<SecondBrain.Core.Configuration.SecondBrainOptions> options) => await Guard(async () =>
            {
                context.Response.Headers.CacheControl = "no-store";
                var request = await KnowledgeEndpoints.ReadJsonAsync<CliPairingPollRequest>(context.Request.Body, 2048, context.RequestAborted);
                return Results.Ok(await service.ClaimAsync(request.Id, request.PollSecret, CredentialService.IsSecure(context, options.CurrentValue), context.RequestAborted));
            })).WithMetadata(new BrowserFacingRequest(), new PasswordRequest());
            endpoints.MapPost(prefix + "/auth/cli/approve", ApproveAsync)
                .WithMetadata(new EndpointPolicy("sessions.pair", SessionOnly: true, BrowserFacing: true), new PasswordRequest());
        }
    }

    private static async Task<IResult> ApproveAsync(HttpContext context, IAntiforgery csrf, LoginService login, ICliPairingRepository pairings)
    {
        return await Guard(async () =>
        {
            try { await csrf.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 400, title: "Invalid antiforgery token."); }
            if (!context.Request.HasFormContentType) return Results.Problem(statusCode: 415, title: "Submit the pairing form.");
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var decision = await login.VerifyAsync(form["Password"].ToString(), AuthEndpoints.Source(context), context.RequestAborted);
            if (!decision.Accepted) return Results.Problem(statusCode: decision.RetryAfterSeconds > 0 ? 429 : 401, title: "The password was not accepted.");
            await pairings.ApprovePairingAsync(form["Code"].ToString(), AuthEndpoints.Identity(context), decision.AccountEpoch ?? throw new AuthorityChangedException(), context.RequestAborted);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Content("CLI pairing approved. You can return to your terminal.", "text/plain");
        });
    }
    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (AuthorityChangedException) { return Results.Problem(statusCode: 403, title: "Session authority changed; sign in again."); }
        catch (CliPairingException exception) { return Results.Problem(statusCode: 409, title: exception.Message); }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException or InvalidDataException)
        { return Results.Problem(statusCode: 400, title: "Invalid pairing request."); }
    }
}
