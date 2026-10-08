using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Problems;
using SecondBrain.Server.Http;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class AccessTests
{
    [Fact]
    public async Task StaticAssetsRequirePublicAccessAssertion()
    {
        await using var factory = new LaneDWebFactory(ConfigureAccess);
        using var client = factory.CreatePrivateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/_framework/blazor.web.js");
        request.Headers.Host = "public.test";
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ProblemTypes.AccessRequired, problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task NoAssertionNoLogin()
    {
        await using var factory = new LaneDWebFactory(ConfigureAccess);
        using var client = factory.CreatePrivateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "public.test";
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ProblemTypes.AccessRequired, problem.GetProperty("type").GetString());
        Assert.Equal(0, factory.KeyRing.HmacCalls);
        using var privateResponse = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, privateResponse.StatusCode);
    }

    [Fact]
    public async Task JwksRefresh()
    {
        using var first = RSA.Create(2048);
        using var second = RSA.Create(2048);
        await using var jwks = await LocalJwksServer.StartAsync(first, "first");
        await using var factory = AccessFactory(jwks);
        using var client = factory.CreatePrivateClient();
        using var initial = await SendAsync(client, Token(first, "first", factory.Clock.GetUtcNow()));
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.Equal(1, jwks.Requests);

        jwks.SetKey(second, "second");
        using var rotated = await SendAsync(client, Token(second, "second", factory.Clock.GetUtcNow()));
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.Equal(2, jwks.Requests);

        var flood = Enumerable.Range(0, 12).Select(async index =>
        {
            using var response = await SendAsync(client, Token(second, "unknown-" + index, factory.Clock.GetUtcNow()));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        });
        await Task.WhenAll(flood);
        Assert.Equal(2, jwks.Requests);
        factory.Clock.Advance(AccessJwksCache.UnknownKidRefreshInterval);
        using var unknown = await SendAsync(client, Token(second, "unknown-after-cooldown", factory.Clock.GetUtcNow()));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(3, jwks.Requests);

        jwks.Fail = true;
        factory.Clock.Advance(TimeSpan.FromDays(1));
        using var lastGood = await SendAsync(client, Token(second, "second", factory.Clock.GetUtcNow()));
        Assert.Equal(HttpStatusCode.OK, lastGood.StatusCode);
        Assert.Equal(4, jwks.Requests);
        var cache = factory.Services.GetRequiredService<SecondBrain.Server.Http.AccessJwksCache>();
        Assert.False(cache.LastRefreshSucceeded);
        Assert.NotEmpty(cache.CurrentKeys);
        Assert.Equal(0, factory.KeyRing.HmacCalls);
    }

    [Fact]
    public async Task JwksInitialFailureIsRateLimited()
    {
        using var rsa = RSA.Create(2048);
        await using var jwks = await LocalJwksServer.StartAsync(rsa, "known");
        jwks.Fail = true;
        await using var factory = AccessFactory(jwks);
        using var client = factory.CreatePrivateClient();
        var flood = Enumerable.Range(0, 8).Select(async index =>
        {
            using var response = await SendAsync(client, Token(rsa, "unknown-" + index, factory.Clock.GetUtcNow()));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        });
        await Task.WhenAll(flood);
        Assert.Equal(1, jwks.Requests);
        jwks.Fail = false;
        factory.Clock.Advance(AccessJwksCache.UnknownKidRefreshInterval);
        using var recovered = await SendAsync(client, Token(rsa, "known", factory.Clock.GetUtcNow()));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(2, jwks.Requests);
    }

    [Fact]
    public async Task CertificateTransportRequiresExplicitControlPlaneAllowance()
    {
        await using var factory = new LaneDWebFactory(ConfigureAccess);
        using var transport = new AccessJwksTransport(factory.Options);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.FetchAsync(
            new Uri("https://test-team.cloudflareaccess.com/cdn-cgi/access/certs"), CancellationToken.None));
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("signature")]
    public async Task InvalidAssertionIsRejectedBeforeCredentials(string invalid)
    {
        using var rsa = RSA.Create(2048);
        using var other = RSA.Create(2048);
        await using var jwks = await LocalJwksServer.StartAsync(rsa, "good");
        await using var factory = AccessFactory(jwks);
        using var client = factory.CreatePrivateClient();
        var now = factory.Clock.GetUtcNow();
        var token = Token(invalid == "signature" ? other : rsa, "good",
            invalid == "expired" ? now - TimeSpan.FromHours(1) : now,
            invalid == "issuer" ? "https://other.cloudflareaccess.com" : "https://test-team.cloudflareaccess.com",
            invalid == "audience" ? "wrong-audience" : "test-audience");
        using var response = await SendAsync(client, token, "sb_bearer.must-not-be-verified");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ProblemTypes.AccessRequired, problem.GetProperty("type").GetString());
        Assert.Equal(0, factory.KeyRing.HmacCalls);
    }

    [Fact]
    public async Task ServiceTokenAssertionUsesSameGate()
    {
        using var rsa = RSA.Create(2048);
        await using var jwks = await LocalJwksServer.StartAsync(rsa, "service");
        await using var factory = AccessFactory(jwks);
        using var client = factory.CreatePrivateClient();
        using var response = await SendAsync(client, Token(rsa, "service", factory.Clock.GetUtcNow(), serviceToken: true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static LaneDWebFactory AccessFactory(LocalJwksServer server) => new(ConfigureAccess, services =>
    {
        services.RemoveAll<IAccessJwksTransport>();
        services.AddSingleton<IAccessJwksTransport>(server);
        foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService) &&
                     descriptor.ImplementationType == typeof(AccessJwksRefreshService)).ToArray())
            services.Remove(descriptor);
    });

    private static void ConfigureAccess(SecondBrainOptions options) => options.Server.CloudflareAccess = new CloudflareAccessOptions
    { PublicHostname = "public.test", TeamDomain = "test-team.cloudflareaccess.com", Audience = "test-audience" };

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string assertion, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = "public.test";
        request.Headers.TryAddWithoutValidation(AccessAuthentication.AssertionHeader, assertion);
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        return await client.SendAsync(request);
    }

    private static string Token(RSA rsa, string kid, DateTimeOffset now,
        string issuer = "https://test-team.cloudflareaccess.com", string audience = "test-audience", bool serviceToken = false)
    {
        var key = new RsaSecurityKey(rsa) { KeyId = kid };
        var claims = new[]
        {
            new System.Security.Claims.Claim("type", "app"),
            new System.Security.Claims.Claim(serviceToken ? "common_name" : "email", serviceToken ? "client.access" : "owner@example.test"),
            new System.Security.Claims.Claim("sub", serviceToken ? "" : "owner")
        };
        var token = new JwtSecurityToken(issuer, audience, claims,
            now.UtcDateTime - TimeSpan.FromMinutes(1), now.UtcDateTime + TimeSpan.FromMinutes(20),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class LocalJwksServer : IAccessJwksTransport, IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly HttpClient client = new();
        private Uri endpoint = null!;
        private string document = string.Empty;
        private int requests;
        public bool Fail { get; set; }
        public int Requests => Volatile.Read(ref requests);
        private LocalJwksServer(WebApplication app) => this.app = app;

        public static async Task<LocalJwksServer> StartAsync(RSA rsa, string kid)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var server = new LocalJwksServer(app);
            server.SetKey(rsa, kid);
            app.MapGet("/certs", async context =>
            {
                Interlocked.Increment(ref server.requests);
                if (server.Fail) { context.Response.StatusCode = 503; return; }
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(Volatile.Read(ref server.document));
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            server.endpoint = new Uri(address + "/certs");
            return server;
        }

        public void SetKey(RSA rsa, string kid)
        {
            var parameters = rsa.ExportParameters(false);
            Volatile.Write(ref document, JsonSerializer.Serialize(new
            {
                keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid,
                    n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent) } }
            }));
        }

        public async ValueTask<string> FetchAsync(Uri certificateEndpoint, CancellationToken cancellationToken)
        {
            Assert.Equal("https://test-team.cloudflareaccess.com/cdn-cgi/access/certs", certificateEndpoint.AbsoluteUri);
            using var response = await client.GetAsync(endpoint, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }
}
