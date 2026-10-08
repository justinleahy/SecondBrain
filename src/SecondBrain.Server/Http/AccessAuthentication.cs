using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Problems;

namespace SecondBrain.Server.Http;

public static class AccessAuthentication
{
    public const string Scheme = "CloudflareAccess";
    public const string AssertionHeader = "Cf-Access-Jwt-Assertion";
}

public sealed class AccessJwtBearerOptions(IOptionsMonitor<SecondBrainOptions> options,
    AccessJwksCache cache, TimeProvider clock) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions jwt) => Configure(Options.DefaultName, jwt);

    public void Configure(string? name, JwtBearerOptions jwt)
    {
        if (name != AccessAuthentication.Scheme) return;
        jwt.MapInboundClaims = false;
        jwt.IncludeErrorDetails = false;
        jwt.RefreshOnIssuerKeyNotFound = false;
        jwt.TimeProvider = clock;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            IssuerValidator = (issuer, _, _) =>
            {
                var access = options.CurrentValue.Server.CloudflareAccess;
                if (access is null || !string.Equals(issuer, AccessJwksCache.Issuer(access), StringComparison.Ordinal))
                    throw new SecurityTokenInvalidIssuerException("Invalid Access issuer.");
                return issuer;
            },
            ValidateAudience = true,
            AudienceValidator = (audiences, _, _) =>
            {
                var audience = options.CurrentValue.Server.CloudflareAccess?.Audience;
                return !string.IsNullOrEmpty(audience) && audiences.Contains(audience, StringComparer.Ordinal);
            },
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeyResolver = (_, _, kid, _) => cache.CurrentKeys.Where(key => string.Equals(key.KeyId, kid, StringComparison.Ordinal)),
            TryAllIssuerSigningKeys = false,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            LifetimeValidator = (notBefore, expires, _, parameters) =>
            {
                var now = clock.GetUtcNow().UtcDateTime;
                return expires is not null && expires > now - parameters.ClockSkew &&
                    (notBefore is null || notBefore <= now + parameters.ClockSkew) &&
                    (notBefore is null || notBefore <= expires);
            }
        };
        jwt.Events = new JwtBearerEvents
        {
            OnMessageReceived = async context =>
            {
                var access = options.CurrentValue.Server.CloudflareAccess;
                if (access is null || !string.Equals(context.Request.Host.Host, access.PublicHostname, StringComparison.OrdinalIgnoreCase))
                {
                    context.NoResult();
                    return;
                }
                var assertions = context.Request.Headers[AccessAuthentication.AssertionHeader];
                if (assertions.Count != 1 || string.IsNullOrEmpty(assertions[0]) || assertions[0]!.Length > 16384)
                {
                    context.Fail("A single bounded Access assertion is required.");
                    return;
                }
                context.Token = assertions[0];
                string? kid;
                try
                {
                    var handler = new JwtSecurityTokenHandler { MaximumTokenSizeInBytes = 16384 };
                    if (!handler.CanReadToken(context.Token)) { context.Fail("Invalid Access assertion."); return; }
                    var token = handler.ReadJwtToken(context.Token);
                    kid = token.Header.Kid;
                    if (string.IsNullOrEmpty(kid) || kid.Length > 256 || token.Header.Alg != SecurityAlgorithms.RsaSha256)
                    {
                        context.Fail("Invalid Access signing key.");
                        return;
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or SecurityTokenException)
                {
                    context.Fail("Invalid Access assertion.");
                    return;
                }
                await cache.EnsureKeysAsync(kid, context.HttpContext.RequestAborted);
            }
        };
    }
}

/// <summary>SEC-3's outer gate runs before the daemon's credential authentication.</summary>
public sealed class AccessGateMiddleware(RequestDelegate next, IOptionsMonitor<SecondBrainOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var access = options.CurrentValue.Server.CloudflareAccess;
        if (access is not null && string.Equals(context.Request.Host.Host, access.PublicHostname, StringComparison.OrdinalIgnoreCase))
        {
            var result = await context.AuthenticateAsync(AccessAuthentication.Scheme);
            if (!result.Succeeded)
            {
                context.Response.Headers.WWWAuthenticate = "Bearer realm=\"CloudflareAccess\"";
                await ProblemResponses.WriteAsync(context, 401, ProblemTypes.AccessRequired, "Cloudflare Access assertion required");
                return;
            }
        }
        await next(context);
    }
}
