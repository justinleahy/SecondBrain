using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Problems;
using SecondBrain.Core.Security;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

/// <summary>SEC-9/SEC-32: credential listing cursors are protected, bound to their caller and listing, and short-lived.</summary>
public sealed class CredentialCursorTests
{
    private const string Password = "test-password";

    [Fact]
    public async Task UnsignedKeysetPositionIsRejected()
    {
        await using var factory = new LaneDWebFactory();
        var createdAt = factory.Clock.GetUtcNow().AddDays(-1);
        using var client = await KeyClientAsync(factory);
        await SeedKeysAsync(factory, 5, createdAt);

        // The pre-correction cursor format: anyone could mint a position by encoding (created_at, id).
        var forged = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(AuthTime.Format(createdAt) + "\nk00002"));
        await AssertInvalid(await client.GetAsync("/keys?after=" + forged + "&limit=2"));
    }

    [Fact]
    public async Task TamperedOrTruncatedCursorIsRejected()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await KeyClientAsync(factory);
        await SeedKeysAsync(factory, 5, factory.Clock.GetUtcNow().AddDays(-1));
        var cursor = await FirstCursorAsync(client, "/keys?limit=2");

        var middle = cursor.Length / 2;
        var flipped = cursor[..middle] + (cursor[middle] == 'A' ? 'B' : 'A') + cursor[(middle + 1)..];
        await AssertInvalid(await client.GetAsync("/keys?after=" + flipped + "&limit=2"));
        await AssertInvalid(await client.GetAsync("/keys?after=" + cursor[..^4] + "&limit=2"));
        await AssertInvalid(await client.GetAsync("/keys?after=" + cursor + "AAAA&limit=2"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/keys?after=" + cursor + "&limit=2")).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%21%21%21")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("overlong")]
    public async Task MalformedOrOverlongCursorIsTyped400(string after)
    {
        await using var factory = new LaneDWebFactory();
        using var client = await KeyClientAsync(factory);
        if (after == "overlong") after = new string('A', 64 * 1024);
        await AssertInvalid(await client.GetAsync("/keys?after=" + after));
        await AssertInvalid(await client.GetAsync("/v1/keys?after=" + after));
    }

    [Fact]
    public async Task CursorExpiresFifteenMinutesAfterIssue()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await KeyClientAsync(factory);
        await SeedKeysAsync(factory, 5, factory.Clock.GetUtcNow().AddDays(-1));
        var cursor = await FirstCursorAsync(client, "/keys?limit=2");

        factory.Clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/keys?after=" + cursor + "&limit=2")).StatusCode);
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        await AssertInvalid(await client.GetAsync("/keys?after=" + cursor + "&limit=2"));
        // A fresh first page issues a fresh cursor; the caller restarts rather than gaining anything.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/keys?after=" + await FirstCursorAsync(client, "/keys?limit=2") + "&limit=2")).StatusCode);
    }

    [Fact]
    public async Task CursorIsBoundToItsListingOperation()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        var createdAt = factory.Clock.GetUtcNow().AddDays(-1);
        await SeedKeysAsync(factory, 5, createdAt);
        await SeedCredentialsAsync(factory, "s", "session", 5, createdAt);
        using var client = factory.CreatePrivateClient();
        await Login(client);

        var keys = await FirstCursorAsync(client, "/keys?limit=2");
        var sessions = await FirstCursorAsync(client, "/auth/sessions?limit=2");
        await AssertInvalid(await client.GetAsync("/auth/sessions?after=" + keys + "&limit=2"));
        await AssertInvalid(await client.GetAsync("/v1/auth/sessions?after=" + keys + "&limit=2"));
        await AssertInvalid(await client.GetAsync("/keys?after=" + sessions + "&limit=2"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/auth/sessions?after=" + sessions + "&limit=2")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/keys?after=" + keys + "&limit=2")).StatusCode);
    }

    [Fact]
    public async Task CursorIsBoundToTheIssuingCredential()
    {
        await using var factory = new LaneDWebFactory();
        using var first = await KeyClientAsync(factory);
        using var second = await KeyClientAsync(factory);
        await SeedKeysAsync(factory, 5, factory.Clock.GetUtcNow().AddDays(-1));
        var cursor = await FirstCursorAsync(first, "/keys?limit=2");

        await AssertInvalid(await second.GetAsync("/keys?after=" + cursor + "&limit=2"));
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/keys?after=" + cursor + "&limit=2")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CursorIsRejectedAfterTheCredentialGenerationOrAccountEpochChanges(bool epoch)
    {
        await using var factory = new LaneDWebFactory();
        var admin = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Admin });
        await SeedKeysAsync(factory, 5, factory.Clock.GetUtcNow().AddDays(-1));
        using var client = Bearer(factory, admin.Plaintext);
        var cursor = await FirstCursorAsync(client, "/keys?limit=2");

        // The key itself stays valid under its new generation or epoch, so only the cursor's binding can fail.
        await factory.Store.QueueWriteAsync(async (connection, transaction, token) => await connection.ExecuteAsync(new CommandDefinition(epoch
            ? "UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='account_epoch'; UPDATE credentials SET account_epoch=account_epoch+1 WHERE id=@id"
            : "UPDATE credentials SET generation=generation+1 WHERE id=@id", new { id = admin.Record.Id }, transaction, cancellationToken: token)));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/keys?limit=2")).StatusCode);
        await AssertInvalid(await client.GetAsync("/keys?after=" + cursor + "&limit=2"));
    }

    [Fact]
    public async Task CursorIsBoundToTheInstance()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await KeyClientAsync(factory);
        await SeedKeysAsync(factory, 5, factory.Clock.GetUtcNow().AddDays(-1));
        var cursor = await FirstCursorAsync(client, "/keys?limit=2");

        // The same key ring and credential on a store with another identity, as after restoring a different instance.
        await factory.Store.QueueWriteAsync(async (connection, transaction, token) => await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE meta SET value=@instance WHERE key='instance_id'", new { instance = Ulid.NewUlid().ToString() }, transaction, cancellationToken: token)));
        await AssertInvalid(await client.GetAsync("/keys?after=" + cursor + "&limit=2"));
    }

    [Fact]
    public async Task CursorEnvelopeUnderAnotherPurposeIsRejected()
    {
        await using var factory = new LaneDWebFactory();
        using var client = await KeyClientAsync(factory);
        await SeedKeysAsync(factory, 5, factory.Clock.GetUtcNow().AddDays(-1));
        var cursor = await FirstCursorAsync(client, "/keys?limit=2");
        var provider = factory.KeyRing.DataProtectionProvider;
        var payload = provider.CreateProtector(KeyRingPurposes.Cursor).CreateProtector(CredentialPaging.CursorPurpose).Unprotect(WebEncoders.Base64UrlDecode(cursor));

        foreach (var purpose in new[] { provider.CreateProtector(KeyRingPurposes.Session), provider.CreateProtector(KeyRingPurposes.Cursor),
            provider.CreateProtector(KeyRingPurposes.Antiforgery).CreateProtector(CredentialPaging.CursorPurpose) })
            await AssertInvalid(await client.GetAsync("/keys?after=" + WebEncoders.Base64UrlEncode(purpose.Protect(payload)) + "&limit=2"));
        var resealed = provider.CreateProtector(KeyRingPurposes.Cursor).CreateProtector(CredentialPaging.CursorPurpose).Protect(payload);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/keys?after=" + WebEncoders.Base64UrlEncode(resealed) + "&limit=2")).StatusCode);
    }

    [Fact]
    public async Task CursorsContinueAcrossRouteAliasesToTheEnd()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        var createdAt = factory.Clock.GetUtcNow().AddDays(-1);
        await SeedKeysAsync(factory, 7, createdAt);
        await SeedCredentialsAsync(factory, "s", "session", 7, createdAt);
        using var client = factory.CreatePrivateClient();
        var own = (await (await Login(client)).Content.ReadFromJsonAsync<CredentialSummary>())!;

        Assert.Equal(Enumerable.Range(1, 7).Select(i => $"k{i:D5}"), await AlternateAsync(client, "/keys", "/v1/keys"));
        Assert.Equal(Enumerable.Range(1, 7).Select(i => $"s{i:D5}").Append(own.Id), await AlternateAsync(client, "/v1/auth/sessions", "/auth/sessions"));
    }

    /// <summary>Follows each next cursor on the other alias, two rows at a time.</summary>
    private static async Task<List<string>> AlternateAsync(HttpClient client, string first, string second)
    {
        var ids = new List<string>();
        string? after = null;
        for (var page = 0; page == 0 || after is not null; page++)
        {
            Assert.True(page < 20);
            using var response = await client.GetAsync((page % 2 == 0 ? first : second) + "?limit=2" + (after is null ? "" : "&after=" + after));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ids.AddRange((await response.Content.ReadFromJsonAsync<CredentialSummary[]>())!.Select(row => row.Id));
            after = response.Headers.TryGetValues("Link", out var links) ? Cursor(Assert.Single(links)) : null;
        }
        return ids;
    }

    private static async Task<string> FirstCursorAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Cursor(Assert.Single(response.Headers.GetValues("Link")));
    }

    private static string Cursor(string link)
    {
        var start = link.IndexOf("after=", StringComparison.Ordinal) + "after=".Length;
        return link[start..link.IndexOf('&', start)];
    }

    private static async Task<HttpClient> KeyClientAsync(LaneDWebFactory factory) =>
        Bearer(factory, (await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Admin })).Plaintext);

    private static HttpClient Bearer(LaneDWebFactory factory, string key)
    {
        var client = factory.CreatePrivateClient(false);
        client.DefaultRequestHeaders.Authorization = new("Bearer", key);
        return client;
    }

    private static Task SeedKeysAsync(LaneDWebFactory factory, int count, DateTimeOffset createdAt) =>
        SeedCredentialsAsync(factory, "k", "api_key", count, createdAt);

    private static Task SeedCredentialsAsync(LaneDWebFactory factory, string prefix, string kind, int count, DateTimeOffset createdAt) =>
        factory.Store.QueueWriteAsync(async (connection, transaction, token) => await connection.ExecuteAsync(new CommandDefinition("""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<@count)
            INSERT INTO credentials (id,name,kind,verifier,scopes,generation,kid,account_epoch,idle_expires_at,absolute_expires_at,created_at)
            SELECT @prefix || printf('%05d', i), 'seeded', @kind, 'AAAA', 'read', 1, 'seed', 1, @future, @future, @createdAt FROM n
            """, new { count, prefix, kind, createdAt = AuthTime.Format(createdAt), future = AuthTime.Format(factory.Clock.GetUtcNow().AddDays(1)) },
            transaction, cancellationToken: token))).AsTask();

    private static async Task<HttpResponseMessage> Login(HttpClient client)
    {
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(Password));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return response;
    }

    private static async Task AssertInvalid(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{response.StatusCode}: {body}");
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(ProblemTypes.InvalidRequest, JsonDocument.Parse(body).RootElement.GetProperty("type").GetString());
        }
    }
}
