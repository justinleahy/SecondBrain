using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Infrastructure.Security;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class AuthRaceTests
{
    [Fact]
    public async Task Login_PasswordResetDuringVerificationRejectsOldPassword()
    {
        var hasher = new CallbackPasswordHasher();
        await using var factory = new LaneDWebFactory(configureServices: services => services.AddSingleton<IPasswordHasher>(hasher));
        await factory.SeedAccountAsync("old-password");
        var repository = factory.Services.GetRequiredService<IAuthRepository>();
        hasher.BeforeVerify = () => ResetPassword(repository, factory, "reset-password-hash");
        using var client = factory.CreatePrivateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://private.test");
        using var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest("old-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(cookie => cookie.StartsWith(CredentialService.CookieName + "=", StringComparison.Ordinal)));
        Assert.Empty((await repository.ListPageAsync("session", activeOnly: false, after: null, limit: 100)).Items);
        Assert.Equal("reset-password-hash", (await repository.GetAccountAsync())!.PasswordHash);
        Assert.Equal(2, await repository.GetEpochAsync());
    }

    [Fact]
    public async Task Login_EpochChangeAfterVerificationPreventsSessionIssuance()
    {
        await using var factory = new LaneDWebFactory();
        await factory.SeedAccountAsync();
        var repository = factory.Services.GetRequiredService<IAuthRepository>();
        var decision = await factory.Services.GetRequiredService<LoginService>().VerifyAsync("test-password", "verified-login");
        Assert.True(decision.Accepted);
        Assert.Equal(1, decision.AccountEpoch);
        await repository.BumpEpochAsync();

        var context = new DefaultHttpContext { RequestServices = factory.Services };
        var credentials = factory.Services.GetRequiredService<CredentialService>();
        await Assert.ThrowsAsync<AuthorityChangedException>(() => credentials.IssueSessionAsync(context, verifiedEpoch: decision.AccountEpoch));
        Assert.Empty((await repository.ListPageAsync("session", activeOnly: false, after: null, limit: 100)).Items);
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task Login_PasswordResetDuringRehashDoesNotOverwriteResetPassword()
    {
        var hasher = new CallbackPasswordHasher();
        await using var factory = new LaneDWebFactory(configureServices: services => services.AddSingleton<IPasswordHasher>(hasher));
        await factory.SeedAccountAsync("old-password");
        var repository = factory.Services.GetRequiredService<IAuthRepository>();
        hasher.RehashNeeded = true;
        hasher.BeforeHash = () => ResetPassword(repository, factory, "reset-password-hash");

        var decision = await factory.Services.GetRequiredService<LoginService>().VerifyAsync("old-password", "rehash-race");
        Assert.False(decision.Accepted);
        Assert.Null(decision.AccountEpoch);
        Assert.Equal("reset-password-hash", (await repository.GetAccountAsync())!.PasswordHash);
        Assert.Equal(2, await repository.GetEpochAsync());
    }

    [Fact]
    public void Keys_IssuanceSnapshotsKidAcrossConcurrentRotation()
    {
        var keyRing = new RotatingOnReadKeyRing();
        var credentials = new CredentialFactory(keyRing, new CallbackPasswordHasher(), TimeProvider.System);
        var issued = credentials.CreateApiKey("rotation", new HashSet<Scope> { Scope.Read }, 1);
        var secret = WebEncoders.Base64UrlDecode(issued.Plaintext[(issued.Plaintext.IndexOf('.', StringComparison.Ordinal) + 1)..]);
        try
        {
            Assert.Equal(1, keyRing.ActiveKidReads);
            Assert.True(CryptographicOperations.FixedTimeEquals(
                keyRing.Sign(issued.Record.Kid, secret), Convert.FromBase64String(issued.Record.Verifier)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static void ResetPassword(IAuthRepository repository, LaneDWebFactory factory, string hash) =>
        Task.Run(() => repository.SaveAccountAsync(new AccountRecord
        {
            PasswordHash = hash,
            PasswordVersion = 1,
            UpdatedAt = factory.Clock.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }, invalidate: true)).GetAwaiter().GetResult();

    private sealed class CallbackPasswordHasher : IPasswordHasher
    {
        public PasswordParameters Parameters { get; } = new(MemoryKiB: 1024, Iterations: 1);
        public Action? BeforeVerify { get; set; }
        public Action? BeforeHash { get; set; }
        public bool RehashNeeded { get; set; }

        public string Hash(string password)
        {
            BeforeHash?.Invoke();
            return "test-hash:" + password;
        }

        public bool Verify(string encodedHash, string password)
        {
            BeforeVerify?.Invoke();
            return encodedHash == "test-hash:" + password;
        }

        public bool NeedsRehash(AccountRecord account) => RehashNeeded;
    }

    private sealed class RotatingOnReadKeyRing : IKeyRing
    {
        private readonly TestKeyRing _inner = new();
        public int ActiveKidReads { get; private set; }
        public string ActiveKid
        {
            get
            {
                ActiveKidReads++;
                var previous = _inner.ActiveKid;
                _inner.Rotate();
                return previous;
            }
        }

        public IDataProtectionProvider DataProtectionProvider => _inner.DataProtectionProvider;
        public byte[] Sign(string kid, ReadOnlySpan<byte> bytes) => _inner.Sign(kid, bytes);
        public bool Verify(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> signature) => _inner.Verify(bytes, signature);
    }
}
