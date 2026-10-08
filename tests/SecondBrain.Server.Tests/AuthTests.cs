using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Infrastructure.Security;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace SecondBrain.Server.Tests;

public sealed class AuthTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Authz_ScopeMatrix()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient(false);
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read });
        client.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<EndpointPolicy>() is not null).ToArray();
        Assert.NotEmpty(endpoints);
        foreach (var endpoint in endpoints)
        {
            var path = endpoint.RoutePattern.RawText!.Replace("{id}", Ulid.NewUlid().ToString(), StringComparison.Ordinal);
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { password = "ignored", name = "ignored", scopes = new[] { "read" }, path = "/proc" }) };
                using var response = await client.SendAsync(request);
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }
        }
        var policy = factory.Services.GetRequiredService<IScopePolicy>();
        Assert.False(policy.IsAllowed("unregistered.operation", Enum.GetValues<Scope>().ToHashSet()));
        Assert.False(policy.IsAllowed("keys.create", new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer }));
    }

    [Fact]
    public async Task Keys_OneHmacAndNoArgon2PerRandomBearer()
    {
        await using var factory = new LaneDWebFactory(configureServices: services =>
        {
            services.AddSingleton<IPasswordHasher, ThrowingPasswordHasher>();
        });
        using var client = factory.CreatePrivateClient(false);
        factory.KeyRing.ResetCounts();
        foreach (var value in new[] { "random", "sb_" + Ulid.NewUlid() + "." + new string('a', 43), "sb_malformed.secret", new string('a', 1024) })
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", value);
            using var response = await client.GetAsync("/keys");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Equal(4, factory.KeyRing.SignCalls);
    }

    [Fact]
    public async Task Keys_CreateListRevokeAndIndependentGenerations()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient(false);
        var admin = await factory.CreateKeyAsync(Enum.GetValues<Scope>().ToHashSet());
        client.DefaultRequestHeaders.Authorization = new("Bearer", admin.Plaintext);
        using var createdResponse = await client.PostAsJsonAsync("/v1/keys", new CreateKeyRequest("worker", ["admin"]));
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<KeyCreatedResponse>())!;
        Assert.StartsWith("sb_" + created.Id + ".", created.Key, StringComparison.Ordinal);
        Assert.DoesNotContain(created.Key, await (await client.GetAsync("/v1/keys")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var revoked = await client.DeleteAsync("/v1/keys/" + created.Id);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        var repo = factory.Services.GetRequiredService<IAuthRepository>();
        Assert.Equal(2, (await repo.FindAsync(created.Id))!.Generation);
        Assert.Equal(1, (await repo.FindAsync(admin.Record.Id))!.Generation);
        client.DefaultRequestHeaders.Authorization = new("Bearer", created.Key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/keys")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", admin.Plaintext);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/keys")).StatusCode);
    }

    [Fact]
    public async Task Keys_ExpiryAndRetainedKid()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient(false);
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Admin }, expiresAt: factory.Clock.GetUtcNow().AddMinutes(1));
        factory.KeyRing.Rotate();
        client.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/keys")).StatusCode);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/keys")).StatusCode);
    }

    [Theory]
    [InlineData(12, true)]
    [InlineData(30 * 24, false)]
    public async Task Sessions_Expiry(int hours, bool idle)
    {
        await using var factory = new LaneDWebFactory(options => { if (!idle) options.Auth.SessionIdleHours = 31 * 24; });
        await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient();
        await Login(client);
        factory.Clock.Advance(TimeSpan.FromHours(hours));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Sessions_IdleSlidingDoesNotExtendAbsoluteExpiry()
    {
        await using var factory = new LaneDWebFactory(options => options.Auth.SessionAbsoluteDays = 1);
        await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient();
        await Login(client);
        for (var i = 0; i < 2; i++)
        {
            factory.Clock.Advance(TimeSpan.FromHours(10));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        }
        factory.Clock.Advance(TimeSpan.FromHours(4));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Sessions_RotationAfterLoginAndStepUp()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient();
        var first = await Login(client);
        var firstCookie = first.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("sb_session=", StringComparison.Ordinal)).Split(';')[0];
        await AddCsrf(client);
        var second = await Login(client);
        var secondBody = (await second.Content.ReadFromJsonAsync<CredentialSummary>())!;
        using var stolen = factory.CreatePrivateClient(false);
        stolen.DefaultRequestHeaders.Add("Cookie", firstCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stolen.GetAsync("/auth/me")).StatusCode);
        await AddCsrf(client);
        using var stepped = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password"));
        Assert.Equal(HttpStatusCode.OK, stepped.StatusCode);
        var third = (await stepped.Content.ReadFromJsonAsync<CredentialSummary>())!;
        Assert.NotEqual(secondBody.Id, third.Id);
        Assert.NotNull((await factory.Services.GetRequiredService<IAuthRepository>().FindAsync(secondBody.Id))!.RevokedAt);
    }

    [Fact]
    public async Task Sessions_LogoutAllEpochRejectsEveryCredential()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var first = factory.CreatePrivateClient();
        using var second = factory.CreatePrivateClient();
        using var keyClient = factory.CreatePrivateClient(false);
        await Login(first); await Login(second);
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Admin });
        keyClient.DefaultRequestHeaders.Authorization = new("Bearer", key.Plaintext);
        await AddCsrf(first);
        Assert.Equal(HttpStatusCode.NoContent, (await first.PostAsync("/auth/logout-all", null)).StatusCode);
        Assert.Equal(2, await factory.Services.GetRequiredService<IAuthRepository>().GetEpochAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.GetAsync("/keys")).StatusCode);
    }

    [Fact]
    public async Task Sessions_StepUpWindowAndAntiforgery()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient();
        await Login(client);
        using var denied = await client.PostAsJsonAsync("/keys", new CreateKeyRequest("worker", ["read"]));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var noCsrf = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password"));
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        await AddCsrf(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/step-up", new LoginRequest("test-password"))).StatusCode);
        await AddCsrf(client);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/keys", new CreateKeyRequest("worker", ["read"]))).StatusCode);
        factory.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/keys", new CreateKeyRequest("worker2", ["read"]))).StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sessions_GenerationAndEpochRejected(bool generation)
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient();
        using var response = await Login(client);
        var summary = (await response.Content.ReadFromJsonAsync<CredentialSummary>())!;
        await factory.Store.QueueWriteAsync(async (connection, transaction, token) =>
            await connection.ExecuteAsync(new CommandDefinition(generation ? "UPDATE credentials SET generation=generation+1 WHERE id=@id" : "UPDATE meta SET value='2' WHERE key='account_epoch'", new { id = summary.Id }, transaction, cancellationToken: token)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Sessions_LogoutAndListRevoke()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var first = factory.CreatePrivateClient();
        using var second = factory.CreatePrivateClient();
        await Login(first);
        var login = await Login(second);
        var row = (await login.Content.ReadFromJsonAsync<CredentialSummary>())!;
        var list = (await first.GetFromJsonAsync<CredentialSummary[]>("/auth/sessions"))!;
        Assert.Equal(2, list.Length);
        await AddCsrf(first);
        Assert.Equal(HttpStatusCode.NoContent, (await first.DeleteAsync("/auth/sessions/" + row.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await first.PostAsync("/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Sessions_SecureCookieCannotBeReplayedOverHttp()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var secure = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://private.test"), AllowAutoRedirect = false });
        using var loggedIn = await Login(secure, origin: "https://private.test");
        var setCookie = loggedIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("sb_session=", StringComparison.Ordinal));
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        using var plain = factory.CreatePrivateClient(false);
        plain.DefaultRequestHeaders.Add("Cookie", setCookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await plain.GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Sessions_BlazorCookiePath()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient();
        var page = await client.GetStringAsync("/login");
        Assert.Contains("name=\"Password\"", page, StringComparison.Ordinal);
        var match = System.Text.RegularExpressions.Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        Assert.True(match.Success, page);
        client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        var result = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Password"] = "test-password", ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value) }));
        Assert.Equal(HttpStatusCode.SeeOther, result.StatusCode);
        Assert.Contains("You are signed in.", await client.GetStringAsync("/"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passwords_LimitRehashAndFixedLockout()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        var login = factory.Services.GetRequiredService<LoginService>();
        Assert.False((await login.VerifyAsync(new string('é', 513), "length")).Accepted);
        for (var i = 0; i < 5; i++)
        {
            Assert.False((await login.VerifyAsync("wrong", "attacker")).Accepted);
            factory.Clock.Advance(TimeSpan.FromSeconds(1 << i));
        }
        var locked = await login.VerifyAsync("test-password", "attacker");
        Assert.True(locked.RetryAfterSeconds > 800);
        factory.Clock.Advance(TimeSpan.FromMinutes(14));
        var probe = await login.VerifyAsync("wrong", "attacker");
        Assert.InRange(probe.RetryAfterSeconds, 1, 60);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await login.VerifyAsync("test-password", "attacker")).Accepted);
        Assert.True((await login.VerifyAsync("test-password", "other-source")).Accepted);
        var repo = factory.Services.GetRequiredService<IAuthRepository>();
        var row = (await repo.GetAccountAsync())!;
        row.PasswordVersion = 0;
        await repo.SaveAccountAsync(row);
        Assert.True((await login.VerifyAsync("test-password", "rehash")).Accepted);
        Assert.Equal(1, (await repo.GetAccountAsync())!.PasswordVersion);
    }

    [Fact]
    public void Passwords_Argon2Calibration()
    {
        var calibration = PasswordHasher.Calibrate();
        output.WriteLine(JsonSerializer.Serialize(calibration));
        Assert.Equal(65536, calibration.Parameters.MemoryKiB);
        Assert.Equal(1, calibration.Parameters.Lanes);
        Assert.InRange(calibration.Parameters.Iterations, 1, 12);
        Assert.True(calibration.SelectedMilliseconds > 0);
        var hash = new PasswordHasher(new PasswordParameters(MemoryKiB: 1024, Iterations: 1)).Hash("test");
        Assert.StartsWith("$argon2id$v=19$", hash, StringComparison.Ordinal);
        Assert.Equal(16, Convert.FromBase64String(hash.Split('$')[4] + "==").Length);
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string password = "test-password", string origin = "http://private.test")
    {
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Add("Origin", origin);
        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(password));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return response;
    }
    private static async Task AddCsrf(HttpClient client)
    {
        var response = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        client.DefaultRequestHeaders.Remove(response.HeaderName);
        client.DefaultRequestHeaders.Add(response.HeaderName, response.Token);
    }
    private sealed class ThrowingPasswordHasher : IPasswordHasher
    {
        public PasswordParameters Parameters => throw new InvalidOperationException("Argon2 called in bearer authentication.");
        public string Hash(string password) => throw new InvalidOperationException();
        public bool Verify(string encodedHash, string password) => throw new InvalidOperationException();
        public bool NeedsRehash(AccountRecord account) => throw new InvalidOperationException();
    }
}
