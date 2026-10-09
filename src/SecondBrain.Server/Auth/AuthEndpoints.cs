using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Problems;
using SecondBrain.Infrastructure.Security;
using SecondBrain.Server.Http;

namespace SecondBrain.Server.Auth;

public sealed record LoginRequest(string Password);
public sealed record CreateKeyRequest(string Name, string[] Scopes, DateTimeOffset? ExpiresAt = null);
public sealed record KeyCreatedResponse(string Id, string Key, string Scopes, string? ExpiresAt);
public sealed record AntiforgeryResponse(string Token, string HeaderName);

public static class AuthEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        foreach (var prefix in new[] { "", "/v1" })
        {
            endpoints.MapPost(prefix + "/auth/login", LoginAsync).WithMetadata(new BrowserFacingRequest(), new PasswordRequest()).Accepts<LoginRequest>("application/json", "application/x-www-form-urlencoded")
                .Produces<CredentialSummary>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(429);
            endpoints.MapGet(prefix + "/auth/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                var tokens = antiforgery.GetAndStoreTokens(context);
                return TypedResults.Ok(new AntiforgeryResponse(tokens.RequestToken!, tokens.HeaderName!));
            });
            endpoints.MapGet(prefix + "/auth/me", (HttpContext context) => TypedResults.Ok(Identity(context)))
                .WithMetadata(new EndpointPolicy("session.current", SessionOnly: true));
            endpoints.MapPost(prefix + "/auth/logout", async (HttpContext context, IAuthRepository repository, IOptionsMonitor<SecondBrainOptions> options) =>
            {
                await repository.RevokeAuthorizedAsync(Identity(context).Id, "session", Identity(context), TimeSpan.FromMinutes(options.CurrentValue.Auth.StepUpMinutes), context.RequestAborted);
                CredentialService.ClearCookie(context);
                return TypedResults.NoContent();
            }).WithMetadata(new EndpointPolicy("sessions.logout", SessionOnly: true));
            endpoints.MapPost(prefix + "/auth/logout-all", async (HttpContext context, IAuthRepository repository, IOptionsMonitor<SecondBrainOptions> options) =>
            {
                await repository.BumpEpochAuthorizedAsync(Identity(context), TimeSpan.FromMinutes(options.CurrentValue.Auth.StepUpMinutes), context.RequestAborted);
                CredentialService.ClearCookie(context);
                return TypedResults.NoContent();
            }).WithMetadata(new EndpointPolicy("sessions.logout-all", SessionOnly: true, StepUp: true));
            // Sessions list only active rows; the filter runs in SQL before each page's limit.
            endpoints.MapGet(prefix + "/auth/sessions", (HttpContext context, IAuthRepository repository, IKeyRing keyRing, TimeProvider clock, string? after, int? limit) =>
                CredentialPaging.ListAsync(context, repository, keyRing, clock, CredentialListing.Sessions, after, limit))
                .WithMetadata(new EndpointPolicy("sessions.list", SessionOnly: true));
            endpoints.MapDelete(prefix + "/auth/sessions/{id}", async Task<Results<NoContent, ProblemHttpResult>> (string id, HttpContext context, IAuthRepository repository,
                IOptionsMonitor<SecondBrainOptions> options, TimeProvider clock) =>
            {
                // Revoking the current session is a logout; revoking another credential is SEC-10 sensitive.
                var identity = Identity(context);
                if (id != identity.Id && !RequestAuthorizationMiddleware.HasFreshStepUp(identity, options.CurrentValue, clock.GetUtcNow()))
                    return TypedResults.Problem(statusCode: 403, type: ProblemTypes.StepUpRequired, title: "Re-authenticate before this action.");
                if (!await repository.RevokeAuthorizedAsync(id, "session", identity, TimeSpan.FromMinutes(options.CurrentValue.Auth.StepUpMinutes), context.RequestAborted)) return TypedResults.Problem(statusCode: 404, type: ProblemTypes.NotFound, title: "Session was not found.");
                if (id == Identity(context).Id) CredentialService.ClearCookie(context);
                return TypedResults.NoContent();
            }).WithMetadata(new EndpointPolicy("sessions.revoke", SessionOnly: true));
            endpoints.MapPost(prefix + "/auth/step-up", async Task<Results<Ok<CredentialSummary>, ProblemHttpResult>> (LoginRequest request, HttpContext context, LoginService login, CredentialService credentials) =>
            {
                var result = await login.VerifyAsync(request.Password ?? "", Source(context), context.RequestAborted);
                if (!result.Accepted) return LoginFailure(context, result);
                var rotated = await credentials.IssueSessionAsync(context, Identity(context), stepUp: true, verifiedEpoch: result.AccountEpoch);
                return TypedResults.Ok(CredentialSummary.From(rotated.Record));
            }).WithMetadata(new EndpointPolicy("sessions.step-up", SessionOnly: true), new PasswordRequest());
            // Key listings keep their history (revoked and earlier-epoch rows), one keyset page at a time.
            endpoints.MapGet(prefix + "/keys", (HttpContext context, IAuthRepository repository, IKeyRing keyRing, TimeProvider clock, string? after, int? limit) =>
                CredentialPaging.ListAsync(context, repository, keyRing, clock, CredentialListing.Keys, after, limit))
                .WithMetadata(new EndpointPolicy("keys.list"));
            endpoints.MapPost(prefix + "/keys", async Task<Results<Created<KeyCreatedResponse>, ProblemHttpResult>> (CreateKeyRequest request, HttpContext context, IAuthRepository repository, CredentialFactory factory, IOptionsMonitor<SecondBrainOptions> options) =>
            {
                if (request.Scopes is null || request.Scopes.Length > 4 || request.Scopes.Length == 0 || request.Scopes.Any(s => !Enum.TryParse<Scope>(s, true, out var scope) || !Enum.IsDefined(scope)))
                    return TypedResults.Problem(statusCode: 400, type: ProblemTypes.InvalidRequest, title: "Scopes must be read, write, infer or admin.");
                CreatedCredential created;
                try { created = factory.CreateApiKey(request.Name, request.Scopes.Select(s => Enum.Parse<Scope>(s, true)).ToHashSet(), await repository.GetEpochAsync(context.RequestAborted), request.ExpiresAt); }
                catch (ArgumentException) { return TypedResults.Problem(statusCode: 400, type: ProblemTypes.InvalidRequest, title: "Credential name or expiry is invalid."); }
                await repository.AddAuthorizedAsync(created.Record, Identity(context), TimeSpan.FromMinutes(options.CurrentValue.Auth.StepUpMinutes), context.RequestAborted);
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Created(prefix + "/keys/" + created.Record.Id, new KeyCreatedResponse(created.Record.Id, created.Plaintext, created.Record.Scopes, created.Record.ExpiresAt));
            }).WithMetadata(new EndpointPolicy("keys.create", StepUp: true));
            endpoints.MapDelete(prefix + "/keys/{id}", async Task<Results<NoContent, ProblemHttpResult>> (string id, HttpContext context, IAuthRepository repository, IOptionsMonitor<SecondBrainOptions> options) =>
                await repository.RevokeAuthorizedAsync(id, "api_key", Identity(context), TimeSpan.FromMinutes(options.CurrentValue.Auth.StepUpMinutes), context.RequestAborted) ? TypedResults.NoContent() : TypedResults.Problem(statusCode: 404, type: ProblemTypes.NotFound, title: "Credential was not found."))
                .WithMetadata(new EndpointPolicy("keys.revoke", StepUp: true));
        }
    }
    private static async Task<IResult> LoginAsync(HttpContext context, LoginService login, CredentialService credentials, IAntiforgery antiforgery)
    {
        LoginRequest? request;
        var form = context.Request.HasFormContentType;
        if (form)
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return TypedResults.Problem(statusCode: 400, type: ProblemTypes.AntiforgeryRejected, title: "The antiforgery token is missing or invalid."); }
            request = new LoginRequest((await context.Request.ReadFormAsync(context.RequestAborted))["Password"].ToString());
        }
        else
        {
            if (!context.Request.HasJsonContentType())
                return TypedResults.Problem(statusCode: 415, type: ProblemTypes.UnsupportedMediaType, title: "Login accepts JSON or a form.");
            try { request = await context.Request.ReadFromJsonAsync<LoginRequest>(context.RequestAborted); }
            catch (JsonException) { return TypedResults.Problem(statusCode: 400, type: ProblemTypes.InvalidRequest, title: "Login requires a password."); }
        }
        if (request?.Password is null) return TypedResults.Problem(statusCode: 400, type: ProblemTypes.InvalidRequest, title: "Login requires a password.");
        var decision = await login.VerifyAsync(request.Password, Source(context), context.RequestAborted);
        if (!decision.Accepted) return LoginFailure(context, decision);
        var previous = context.Items[typeof(AuthenticatedCredential)] as AuthenticatedCredential;
        var issued = await credentials.IssueSessionAsync(context, previous, verifiedEpoch: decision.AccountEpoch);
        context.Response.Headers.CacheControl = "no-store";
        if (form) { context.Response.StatusCode = 303; context.Response.Headers.Location = "/"; return TypedResults.Empty; }
        return TypedResults.Ok(CredentialSummary.From(issued.Record));
    }
    private static ProblemHttpResult LoginFailure(HttpContext context, LoginDecision decision)
    {
        if (decision.RetryAfterSeconds > 0)
        {
            context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return TypedResults.Problem(statusCode: 429, type: ProblemTypes.LimitExceeded, title: "Login is temporarily delayed.");
        }
        return TypedResults.Problem(statusCode: 401, type: ProblemTypes.AuthenticationRequired, title: "The password was not accepted.");
    }
    internal static string Source(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    public static AuthenticatedCredential Identity(HttpContext context) => (AuthenticatedCredential)context.Items[typeof(AuthenticatedCredential)]!;
}
