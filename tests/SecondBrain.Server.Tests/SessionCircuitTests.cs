using System.Globalization;
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Authorization;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Limits;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class SessionCircuitTests
{
    [Fact]
    public async Task Revalidation_AnonymousDoesNotResolvePersistence()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        Assert.True(await SessionRevalidationProvider.IsValidAsync(new ClaimsPrincipal(new ClaimsIdentity()), services));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("epoch")]
    [InlineData("revoked")]
    [InlineData("idle")]
    [InlineData("absolute")]
    public async Task Revalidation_RejectsCapturedAuthorityAfterInvalidation(string invalidation)
    {
        await using var factory = new LaneDWebFactory(options =>
        {
            if (invalidation == "absolute") options.Auth.SessionIdleHours = 31 * 24;
        });
        var session = await CreateSessionAsync(factory);
        var principal = Principal(session.Record);
        Assert.True(await SessionRevalidationProvider.IsValidAsync(principal, factory.Services));

        await InvalidateAsync(factory, session.Record.Id, invalidation);

        Assert.False(await SessionRevalidationProvider.IsValidAsync(principal, factory.Services));
    }

    [Fact]
    public async Task Revalidation_MissingGenerationRejectsAuthenticatedPrincipal()
    {
        await using var factory = new LaneDWebFactory();
        var session = await CreateSessionAsync(factory);
        var claims = Principal(session.Record).Claims.Where(claim => claim.Type != "generation");
        var incomplete = new ClaimsPrincipal(new ClaimsIdentity(claims, CredentialAuthenticationHandler.SchemeName));
        Assert.False(await SessionRevalidationProvider.IsValidAsync(incomplete, factory.Services));
    }

    [Fact]
    public async Task Circuit_ApiKeyCannotConnectAsInteractiveSession()
    {
        await using var factory = new LaneDWebFactory();
        var key = await factory.CreateKeyAsync(Enum.GetValues<Scope>().ToHashSet());
        var principal = Principal(key.Record);
        Assert.False(await SessionRevalidationProvider.IsValidAsync(principal, factory.Services));
        var admission = factory.Services.GetRequiredService<IAdmissionController>();
        using var handler = Handler(factory, principal, admission);

        // These handlers do not inspect Circuit or inbound context. Null isolates the
        // real authorization and admission callbacks from framework transport setup.
        await Assert.ThrowsAsync<AuthorityChangedException>(() => handler.OnCircuitOpenedAsync(null!, CancellationToken.None));

        Assert.Equal(0, admission.Snapshot.ActiveReservations);
    }

    [Fact]
    public async Task Circuit_ConcurrencyPermitIsReservedUntilDisposal()
    {
        await using var factory = new LaneDWebFactory(options => options.Limits.PerCredential.Circuits = 1);
        var session = await CreateSessionAsync(factory);
        var principal = Principal(session.Record);
        var admission = factory.Services.GetRequiredService<IAdmissionController>();
        using var first = Handler(factory, principal, admission);
        using var second = Handler(factory, principal, admission);

        await first.OnCircuitOpenedAsync(null!, CancellationToken.None);
        Assert.Equal(1, admission.Snapshot.ActiveReservations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.OnCircuitOpenedAsync(null!, CancellationToken.None));
        Assert.Equal(1, admission.Snapshot.ActiveReservations);

        first.Dispose();
        first.Dispose();
        Assert.Equal(0, admission.Snapshot.ActiveReservations);
        using var third = Handler(factory, principal, admission);
        await third.OnCircuitOpenedAsync(null!, CancellationToken.None);
        Assert.Equal(1, admission.Snapshot.ActiveReservations);
        await third.OnCircuitClosedAsync(null!, CancellationToken.None);
        Assert.Equal(0, admission.Snapshot.ActiveReservations);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("epoch")]
    [InlineData("revoked")]
    [InlineData("idle")]
    public async Task Circuit_StaleInboundActivityDoesNotRunApplicationCode(string invalidation)
    {
        await using var factory = new LaneDWebFactory();
        var session = await CreateSessionAsync(factory);
        var admission = factory.Services.GetRequiredService<IAdmissionController>();
        using var handler = Handler(factory, Principal(session.Record), admission);
        await handler.OnCircuitOpenedAsync(null!, CancellationToken.None);
        var nextCalls = 0;
        var inbound = handler.CreateInboundActivityHandler(_ =>
        {
            nextCalls++;
            return Task.CompletedTask;
        });

        await InvalidateAsync(factory, session.Record.Id, invalidation);

        await Assert.ThrowsAsync<AuthorityChangedException>(() => inbound(null!));
        Assert.Equal(0, nextCalls);
        handler.Dispose();
        Assert.Equal(0, admission.Snapshot.ActiveReservations);
    }

    [Fact]
    public async Task Circuit_ValidActivityRefreshesIdleExpiryAndDispatches()
    {
        await using var factory = new LaneDWebFactory();
        var session = await CreateSessionAsync(factory);
        var admission = factory.Services.GetRequiredService<IAdmissionController>();
        using var handler = Handler(factory, Principal(session.Record), admission);
        await handler.OnCircuitOpenedAsync(null!, CancellationToken.None);
        factory.Clock.Advance(TimeSpan.FromHours(1));
        var nextCalls = 0;
        var inbound = handler.CreateInboundActivityHandler(_ =>
        {
            nextCalls++;
            return Task.CompletedTask;
        });

        await inbound(null!);

        Assert.Equal(1, nextCalls);
        var row = (await factory.Services.GetRequiredService<IAuthRepository>().FindAsync(session.Record.Id))!;
        Assert.Equal(factory.Clock.GetUtcNow(), DateTimeOffset.Parse(row.LastUsedAt!, CultureInfo.InvariantCulture));
        Assert.Equal(factory.Clock.GetUtcNow().AddHours(12), DateTimeOffset.Parse(row.IdleExpiresAt!, CultureInfo.InvariantCulture));
        Assert.Equal(session.Record.AbsoluteExpiresAt, row.AbsoluteExpiresAt);
    }

    private static SessionCircuitHandler Handler(LaneDWebFactory factory, ClaimsPrincipal principal, IAdmissionController admission) =>
        new(new CapturedAuthenticationStateProvider(principal), factory.Services, admission, factory.Options, factory.Clock);

    private static Task<CreatedCredential> CreateSessionAsync(LaneDWebFactory factory) =>
        factory.Services.GetRequiredService<CredentialService>().IssueSessionAsync(new DefaultHttpContext
        {
            RequestServices = factory.Services,
        });

    private static ClaimsPrincipal Principal(CredentialRecord credential) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, credential.Id),
        new Claim("credential_id", credential.Id),
        new Claim("credential_kind", credential.Kind),
        new Claim("generation", credential.Generation.ToString(CultureInfo.InvariantCulture)),
        new Claim("account_epoch", credential.AccountEpoch.ToString(CultureInfo.InvariantCulture)),
    ], CredentialAuthenticationHandler.SchemeName));

    private static async Task InvalidateAsync(LaneDWebFactory factory, string id, string invalidation)
    {
        var repository = factory.Services.GetRequiredService<IAuthRepository>();
        switch (invalidation)
        {
            case "generation":
                await factory.Store.QueueWriteAsync(async (connection, transaction, token) =>
                    await connection.ExecuteAsync(new CommandDefinition("UPDATE credentials SET generation=generation+1 WHERE id=@id", new { id }, transaction, cancellationToken: token)));
                break;
            case "epoch":
                await repository.BumpEpochAsync();
                break;
            case "revoked":
                Assert.True(await repository.RevokeAsync(id, "session"));
                break;
            case "idle":
                factory.Clock.Advance(TimeSpan.FromHours(12));
                break;
            case "absolute":
                factory.Clock.Advance(TimeSpan.FromDays(30));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidation));
        }
    }

    private sealed class CapturedAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }
}
