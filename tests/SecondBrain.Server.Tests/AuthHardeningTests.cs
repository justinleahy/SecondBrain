using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Problems;
using SecondBrain.Infrastructure.Security;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

/// <summary>Regressions for bounded password bodies, revocation step-up, complete credential listings and the lockout history cap.</summary>
public sealed class AuthHardeningTests
{
    private const string Password = "test-password";

    // A1: password bodies are admitted and bounded before any parsing.

    [Theory]
    [InlineData("/auth/login", "json")]
    [InlineData("/v1/auth/login", "json")]
    [InlineData("/auth/login", "form")]
    [InlineData("/v1/auth/login", "chunked")]
    [InlineData("/auth/login", "chunked-form")]
    public async Task Login_OversizedBodyIsRejectedBeforeParsingOrVerification(string path, string representation)
    {
        var hasher = new CountingPasswordHasher();
        await using var factory = new LaneDWebFactory(configureServices: services => services.AddSingleton<IPasswordHasher>(hasher));
        await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient();
        await AddCsrf(client);
        client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        var padding = new string('a', PasswordRequestMiddleware.MaxBodyBytes);
        HttpContent content = representation switch
        {
            "json" => JsonContent.Create(new { password = Password, padding }),
            "form" => new FormUrlEncodedContent(new Dictionary<string, string> { ["Password"] = Password, ["padding"] = padding }),
            "chunked" => Unbuffered(JsonSerializer.SerializeToUtf8Bytes(new { password = Password, padding }), "application/json"),
            _ => Unbuffered(Encoding.ASCII.GetBytes("Password=" + Password + "&padding=" + padding), "application/x-www-form-urlencoded"),
        };
        using var response = await client.PostAsync(path, content);

        await AssertProblem(response, HttpStatusCode.RequestEntityTooLarge, ProblemTypes.RequestTooLarge);
        Assert.Equal(0, hasher.VerifyCalls);
        Assert.Equal(0L, await LoginAttemptCount(factory));
    }

    [Fact]
    public async Task StepUp_OversizedBodyIsRejectedBeforeAntiforgeryAndBinding()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient();
        await Login(client);
        // No antiforgery token: the bound runs before the session's antiforgery read.
        using var response = await client.PostAsJsonAsync("/auth/step-up", new { password = Password, padding = new string('a', PasswordRequestMiddleware.MaxBodyBytes) });
        await AssertProblem(response, HttpStatusCode.RequestEntityTooLarge, ProblemTypes.RequestTooLarge);
    }

    [Fact]
    public async Task Login_BadOriginStillWinsOverAnOversizedBody()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://attacker.test");
        using var response = await client.PostAsJsonAsync("/auth/login", new { password = Password, padding = new string('a', 64 * 1024) });
        await AssertProblem(response, HttpStatusCode.Forbidden, ProblemTypes.OriginRejected);
        Assert.Equal(0, factory.KeyRing.HmacCalls);
    }

    [Fact]
    public async Task Login_MaximumPasswordFitsEveryRepresentation()
    {
        // 1,024 UTF-8 bytes; every character escaped as \u00XX in JSON and %XX in a form.
        var password = new string('&', 1024);
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync(password);
        var escaped = "{\"password\":\"" + string.Concat(password.Select(c => $"\\u{(int)c:x4}")) + "\"}";
        Assert.True(Encoding.UTF8.GetByteCount(escaped) > 6 * 1024);

        using var json = factory.CreatePrivateClient();
        json.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using (var accepted = await json.PostAsync("/auth/login", new StringContent(escaped, Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var unbuffered = factory.CreatePrivateClient();
        unbuffered.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using (var chunked = await unbuffered.PostAsync("/v1/auth/login", Unbuffered(Encoding.UTF8.GetBytes(escaped), "application/json")))
            Assert.Equal(HttpStatusCode.OK, chunked.StatusCode);

        using var form = factory.CreatePrivateClient();
        await AddCsrf(form);
        form.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using (var posted = await form.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Password"] = password })))
            Assert.Equal(HttpStatusCode.SeeOther, posted.StatusCode);

        using var tooLong = factory.CreatePrivateClient();
        tooLong.DefaultRequestHeaders.Add("Origin", "http://private.test");
        var overLimit = "{\"password\":\"" + string.Concat((password + "&").Select(c => $"\\u{(int)c:x4}")) + "\"}";
        using var rejected = await tooLong.PostAsync("/auth/login", new StringContent(overLimit, Encoding.UTF8, "application/json"));
        await AssertProblem(rejected, HttpStatusCode.Unauthorized, ProblemTypes.AuthenticationRequired);
    }

    [Fact]
    public async Task Login_UnsupportedContentTypeIsTyped415()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var response = await client.PostAsync("/auth/login", new StringContent("{\"password\":\"x\"}", Encoding.UTF8, "text/plain"));
        await AssertProblem(response, HttpStatusCode.UnsupportedMediaType, ProblemTypes.UnsupportedMediaType);
    }

    [Fact]
    public async Task Login_PerSourceAdmissionRejectsWithoutWaitingAndReleases()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        _ = factory.CreatePrivateClient();
        var held = Enumerable.Range(0, PasswordRequestMiddleware.PerSourceConcurrency).Select(_ => new HangingBody()).ToArray();
        var pending = held.Select(body => Send(factory, "10.0.0.1", body)).ToArray();
        await Task.WhenAll(held.Select(body => body.Reading.Task));

        var rejected = await Send(factory, "10.0.0.1", Body(Password));
        Assert.Equal(429, rejected.Response.StatusCode);
        Assert.Equal("1", rejected.Response.Headers.RetryAfter.ToString());
        Assert.Equal(0L, await LoginAttemptCount(factory));
        Assert.Equal(200, (await Send(factory, "10.0.0.2", Body(Password))).Response.StatusCode);

        foreach (var body in held) body.Release.SetResult();
        Assert.All(await Task.WhenAll(pending), context => Assert.Equal(400, context.Response.StatusCode));
        Assert.Equal(200, (await Send(factory, "10.0.0.1", Body(Password))).Response.StatusCode);
    }

    [Fact]
    public async Task Login_GlobalAdmissionBoundsDistinctSources()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        _ = factory.CreatePrivateClient();
        var held = Enumerable.Range(0, PasswordRequestMiddleware.GlobalConcurrency).Select(_ => new HangingBody()).ToArray();
        var pending = held.Select((body, index) => Send(factory, "10.0.1." + index, body)).ToArray();
        await Task.WhenAll(held.Select(body => body.Reading.Task));

        var rejected = await Send(factory, "10.0.2.1", Body(Password));
        Assert.Equal(429, rejected.Response.StatusCode);

        foreach (var body in held) body.Release.SetResult();
        await Task.WhenAll(pending);
        Assert.Equal(200, (await Send(factory, "10.0.2.1", Body(Password))).Response.StatusCode);
    }

    [Fact]
    public async Task Login_StalledBodyTimesOutAndReleasesAdmission()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        _ = factory.CreatePrivateClient();
        var stalled = new HangingBody();
        var pending = Send(factory, "10.0.0.9", stalled);
        await stalled.Reading.Task;
        factory.Clock.Advance(PasswordRequestMiddleware.BodyTimeout);
        Assert.Equal(408, (await pending).Response.StatusCode);
        Assert.Equal(200, (await Send(factory, "10.0.0.9", Body(Password))).Response.StatusCode);
    }

    // A2: other-session and account-wide revocation need a fresh step-up; self-logout does not.

    [Fact]
    public async Task LogoutAll_RequiresFreshStepUp()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        var repository = factory.Services.GetRequiredService<IAuthRepository>();
        using var first = factory.CreatePrivateClient();
        using var second = factory.CreatePrivateClient();
        await Login(first); await Login(second);
        await AddCsrf(first);
        await AssertProblem(await first.PostAsync("/v1/auth/logout-all", null), HttpStatusCode.Forbidden, ProblemTypes.StepUpRequired);
        Assert.Equal(1, await repository.GetEpochAsync());
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/auth/me")).StatusCode);

        await StepUp(first);
        factory.Clock.Advance(TimeSpan.FromMinutes(10));
        await AssertProblem(await first.PostAsync("/auth/logout-all", null), HttpStatusCode.Forbidden, ProblemTypes.StepUpRequired);
        Assert.Equal(1, await repository.GetEpochAsync());

        await StepUp(first);
        Assert.Equal(HttpStatusCode.NoContent, (await first.PostAsync("/auth/logout-all", null)).StatusCode);
        Assert.Equal(2, await repository.GetEpochAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task SessionRevocation_OtherNeedsStepUpButSelfDoesNot()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var first = factory.CreatePrivateClient();
        using var second = factory.CreatePrivateClient();
        var own = await Summary(await Login(first));
        var other = await Summary(await Login(second));
        await AddCsrf(first);
        await AssertProblem(await first.DeleteAsync("/v1/auth/sessions/" + other.Id), HttpStatusCode.Forbidden, ProblemTypes.StepUpRequired);
        Assert.Null((await factory.Services.GetRequiredService<IAuthRepository>().FindAsync(other.Id))!.RevokedAt);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/auth/me")).StatusCode);

        await AddCsrf(second);
        Assert.Equal(HttpStatusCode.NoContent, (await second.DeleteAsync("/auth/sessions/" + other.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/auth/me")).StatusCode);

        using var third = factory.CreatePrivateClient();
        var target = await Summary(await Login(third));
        var stepped = await StepUp(first);
        Assert.NotEqual(own.Id, stepped.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await first.DeleteAsync("/auth/sessions/" + target.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await third.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/auth/me")).StatusCode);
    }

    // A3: active sessions and key history are listed completely, one keyset page at a time.

    [Fact]
    public async Task Sessions_ListingPagesPastHistoricalRowsAndRevokesALaterPage()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        using var client = factory.CreatePrivateClient();
        var own = await Summary(await Login(client));
        var now = factory.Clock.GetUtcNow();
        await SeedCredentialsAsync(factory, "h", "session", 1100, now.AddDays(-2), historical: true);
        await SeedCredentialsAsync(factory, "a", "session", 1100, now.AddDays(-1), historical: false);

        var (ids, links) = await ListAllAsync(client, "/auth/sessions");
        Assert.Equal(1101, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 1100).Select(i => $"a{i:D5}").Append(own.Id), ids);
        Assert.Equal(11, links.Count);
        Assert.All(links, link => Assert.StartsWith("</auth/sessions?after=", link, StringComparison.Ordinal));

        using (var aliased = await client.GetAsync("/v1/auth/sessions?limit=500"))
        {
            Assert.Equal(500, (await aliased.Content.ReadFromJsonAsync<CredentialSummary[]>())!.Length);
            var link = Assert.Single(aliased.Headers.GetValues("Link"));
            Assert.StartsWith("</v1/auth/sessions?after=", link, StringComparison.Ordinal);
            Assert.EndsWith("&limit=500>; rel=\"next\"", link, StringComparison.Ordinal);
        }

        await StepUp(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/auth/sessions/a01100")).StatusCode);
        var (after, _) = await ListAllAsync(client, "/auth/sessions");
        Assert.Equal(1100, after.Count);
        Assert.DoesNotContain("a01100", after);
        Assert.DoesNotContain(own.Id, after);
    }

    [Fact]
    public async Task Keys_ListingKeepsHistoryAndReachesNewKeysPastTheOldCap()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient(false);
        await SeedCredentialsAsync(factory, "k", "api_key", 1100, factory.Clock.GetUtcNow().AddDays(-1), historical: true);
        var admin = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Admin });
        client.DefaultRequestHeaders.Authorization = new("Bearer", admin.Plaintext);
        using var created = await client.PostAsJsonAsync("/keys", new CreateKeyRequest("newest", ["read"]));
        var key = (await created.Content.ReadFromJsonAsync<KeyCreatedResponse>())!;

        var (ids, _) = await ListAllAsync(client, "/v1/keys");
        Assert.Equal(1102, ids.Count);
        Assert.Contains(key.Id, ids);
        Assert.Contains(admin.Record.Id, ids);
        Assert.Contains("k00001", ids);
    }

    [Theory]
    [InlineData("?after=not-a-cursor")]
    [InlineData("?after=MjAyNg")]
    [InlineData("?limit=0")]
    [InlineData("?limit=501")]
    public async Task Listing_RejectsMalformedPagingParameters(string query)
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient(false);
        var admin = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Admin });
        client.DefaultRequestHeaders.Authorization = new("Bearer", admin.Plaintext);
        await AssertProblem(await client.GetAsync("/keys" + query), HttpStatusCode.BadRequest, ProblemTypes.InvalidRequest);
    }

    // A4: the lockout is rebuilt from the tail after the latest success, never from the oldest capped rows.

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lockout_HoldsAfterManySuccessesAboveTheHistoryCap(bool identicalTimestamps)
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        var now = factory.Clock.GetUtcNow();
        var successes = Enumerable.Range(0, 1500).Select(i => identicalTimestamps ? now : now.AddMinutes(-30).AddMilliseconds(i));
        await SeedAttemptsAsync(factory, "busy", successes.Select(at => (at, true)).Concat(Enumerable.Repeat((now, false), 5)));

        var locked = await factory.Services.GetRequiredService<LoginService>().VerifyAsync(Password, "busy");
        Assert.False(locked.Accepted);
        Assert.True(locked.RetryAfterSeconds > 800, locked.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Lockout_ExponentialDelayStillAppliesAboveTheHistoryCap()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        var now = factory.Clock.GetUtcNow();
        await SeedAttemptsAsync(factory, "busy", Enumerable.Repeat((now, true), 1500).Concat(Enumerable.Repeat((now, false), 4)));
        var login = factory.Services.GetRequiredService<LoginService>();

        Assert.InRange((await login.VerifyAsync(Password, "busy")).RetryAfterSeconds, 1, 8);
        factory.Clock.Advance(TimeSpan.FromSeconds(8));
        Assert.True((await login.VerifyAsync(Password, "busy")).Accepted);
    }

    [Fact]
    public async Task Lockout_FailsClosedWhenTheTailFillsTheCap()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        var now = factory.Clock.GetUtcNow();
        var failures = Enumerable.Range(0, LoginService.AttemptHistoryLimit).Select(i => (now.AddMinutes(-40).AddSeconds(i * 2), false));
        await SeedAttemptsAsync(factory, "flood", failures);
        var newest = now.AddMinutes(-40).AddSeconds((LoginService.AttemptHistoryLimit - 1) * 2);
        var login = factory.Services.GetRequiredService<LoginService>();

        var locked = await login.VerifyAsync(Password, "flood");
        Assert.False(locked.Accepted);
        Assert.Equal((int)Math.Ceiling((newest.AddMinutes(15) - now).TotalSeconds), locked.RetryAfterSeconds);
        factory.Clock.Advance(newest.AddMinutes(15) - now);
        Assert.True((await login.VerifyAsync(Password, "flood")).Accepted);
    }

    private static async Task<(List<string> Ids, List<string> Links)> ListAllAsync(HttpClient client, string path)
    {
        var ids = new List<string>();
        var links = new List<string>();
        for (string? next = path; next is not null;)
        {
            using var response = await client.GetAsync(next);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ids.AddRange((await response.Content.ReadFromJsonAsync<CredentialSummary[]>())!.Select(row => row.Id));
            next = null;
            if (response.Headers.TryGetValues("Link", out var values))
            {
                var link = Assert.Single(values);
                links.Add(link);
                Assert.EndsWith(">; rel=\"next\"", link, StringComparison.Ordinal);
                next = link[1..link.IndexOf('>', StringComparison.Ordinal)];
            }
            Assert.True(links.Count < 100);
        }
        return (ids, links);
    }

    private static Task SeedCredentialsAsync(LaneDWebFactory factory, string prefix, string kind, int count, DateTimeOffset createdAt, bool historical)
    {
        var now = factory.Clock.GetUtcNow();
        return factory.Store.QueueWriteAsync(async (connection, transaction, token) => await connection.ExecuteAsync(new CommandDefinition("""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<@count)
            INSERT INTO credentials (id,name,kind,verifier,scopes,generation,kid,account_epoch,idle_expires_at,absolute_expires_at,created_at,expires_at,revoked_at)
            SELECT @prefix || printf('%05d', i), 'seeded', @kind, 'AAAA', 'read', 1, 'seed',
              CASE WHEN @historical AND i % 5 = 1 THEN 0 ELSE 1 END,
              CASE WHEN @historical AND i % 5 = 2 THEN @past ELSE @future END,
              CASE WHEN @historical AND i % 5 = 3 THEN @past ELSE @future END,
              @createdAt,
              CASE WHEN @historical AND i % 5 = 4 THEN @past END,
              CASE WHEN @historical AND i % 5 = 0 THEN @past END
            FROM n
            """, new { count, prefix, kind, historical, createdAt = AuthTime.Format(createdAt), past = AuthTime.Format(now.AddMinutes(-1)), future = AuthTime.Format(now.AddDays(1)) },
            transaction, cancellationToken: token))).AsTask();
    }

    private static Task SeedAttemptsAsync(LaneDWebFactory factory, string source, IEnumerable<(DateTimeOffset At, bool Success)> attempts) =>
        factory.Store.QueueWriteAsync(async (connection, transaction, token) => await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO login_attempts(source,at,success) VALUES(@source,@at,@success)",
            attempts.Select(attempt => new { source, at = AuthTime.Format(attempt.At), success = attempt.Success }).ToArray(),
            transaction, cancellationToken: token))).AsTask();

    private static async Task<long> LoginAttemptCount(LaneDWebFactory factory)
    {
        await using var lease = await factory.Store.OpenReadConnectionAsync();
        return await lease.Connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM login_attempts");
    }

    private static Task<HttpContext> Send(LaneDWebFactory factory, string remote, Stream body) => factory.Server.SendAsync(context =>
    {
        context.Request.Method = "POST";
        context.Request.Path = "/auth/login";
        context.Request.Host = new("private.test");
        context.Request.Scheme = "http";
        context.Request.ContentType = "application/json";
        context.Request.Headers.Origin = "http://private.test";
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Body = body;
    });

    private static MemoryStream Body(string password) => new(JsonSerializer.SerializeToUtf8Bytes(new { password }));

    private static StreamContent Unbuffered(byte[] bytes, string mediaType)
    {
        var content = new StreamContent(new UnseekableStream(bytes));
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client)
    {
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(Password));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return response;
    }

    private static async Task<CredentialSummary> Summary(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<CredentialSummary>())!;

    private static async Task<CredentialSummary> StepUp(HttpClient client)
    {
        await AddCsrf(client);
        using var stepped = await client.PostAsJsonAsync("/auth/step-up", new LoginRequest(Password));
        Assert.Equal(HttpStatusCode.OK, stepped.StatusCode);
        await AddCsrf(client);
        return (await stepped.Content.ReadFromJsonAsync<CredentialSummary>())!;
    }

    private static async Task AddCsrf(HttpClient client)
    {
        var response = (await client.GetFromJsonAsync<AntiforgeryResponse>("/auth/antiforgery"))!;
        client.DefaultRequestHeaders.Remove(response.HeaderName);
        client.DefaultRequestHeaders.Add(response.HeaderName, response.Token);
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string type)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(type, JsonDocument.Parse(body).RootElement.GetProperty("type").GetString());
        }
    }

    private sealed class CountingPasswordHasher : IPasswordHasher
    {
        private readonly PasswordHasher inner = new(new PasswordParameters(Version: 1, MemoryKiB: 1024, Iterations: 1, Lanes: 1));
        private int verifyCalls;
        public int VerifyCalls => Volatile.Read(ref verifyCalls);
        public PasswordParameters Parameters => inner.Parameters;
        public string Hash(string password) => inner.Hash(password);
        public bool Verify(string encodedHash, string password) { Interlocked.Increment(ref verifyCalls); return inner.Verify(encodedHash, password); }
        public bool NeedsRehash(AccountRecord account) => inner.NeedsRehash(account);
    }

    /// <summary>Forces a request without a Content-Length.</summary>
    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }

    /// <summary>A request body that stalls after its first read until released, then ends.</summary>
    private sealed class HangingBody : Stream
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reading.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
