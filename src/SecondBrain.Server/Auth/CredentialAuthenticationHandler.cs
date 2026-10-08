using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Server.Http;

namespace SecondBrain.Server.Auth;

public sealed class CredentialAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "SecondBrainCredentials";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        var cookie = Request.Cookies[CredentialService.CookieName];
        if (authorization.Length == 0 && cookie is null) return AuthenticateResult.NoResult();
        // Resolve persistence only for a credential-bearing request; liveness never depends on stores.
        var service = Context.RequestServices.GetRequiredService<CredentialService>();
        AuthenticatedCredential? identity;
        if (authorization.Length > 0)
        {
            if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.Fail("Invalid authentication scheme.");
            identity = await service.AuthenticateKeyAsync(authorization[7..], Context.RequestAborted);
        }
        else
        {
            var config = Context.RequestServices.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue;
            identity = await service.AuthenticateSessionAsync(cookie!, CredentialService.IsSecure(Context, config), Context.RequestAborted);
        }
        if (identity is null) return AuthenticateResult.Fail("Invalid or expired credential.");
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, identity.Id), new("credential_id", identity.Id),
            new("credential_kind", identity.Kind), new("generation", identity.Generation.ToString(CultureInfo.InvariantCulture)),
            new("account_epoch", identity.AccountEpoch.ToString(CultureInfo.InvariantCulture)),
        };
        claims.AddRange(identity.Scopes.Select(scope => new Claim("scope", scope.ToString().ToLowerInvariant())));
        if (identity.SteppedUpAt is not null) claims.Add(new("stepped_up_at", identity.SteppedUpAt));
        Context.Items[typeof(AuthenticatedCredential)] = identity;
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => ProblemResponses.WriteAsync(Context, 401, AuthProblemTypes.AuthenticationRequired, "A valid credential is required.");
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => ProblemResponses.WriteAsync(Context, 403, SecondBrain.Core.Problems.ProblemTypes.ScopeDenied, "Required scope is missing.");
}

/// <summary>Additional stable problem types until the orchestrator extends the frozen central list.</summary>
public static class AuthProblemTypes
{
    public const string AuthenticationRequired = "https://secondbrain.dev/problems/authentication-required";
    public const string StepUpRequired = "https://secondbrain.dev/problems/step-up-required";
    public const string AntiforgeryRejected = "https://secondbrain.dev/problems/antiforgery-rejected";
    public const string InvalidRequest = "https://secondbrain.dev/problems/invalid-request";
    public const string NotFound = "https://secondbrain.dev/problems/not-found";
}
