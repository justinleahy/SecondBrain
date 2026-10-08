using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Server.Limits;

namespace SecondBrain.Server.Auth;

/// <summary>Revalidates the captured circuit authority; repository access is lazy for anonymous login circuits.</summary>
public sealed class SessionRevalidationProvider(ILoggerFactory loggerFactory, IServiceProvider services)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);
    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken) =>
        IsValidAsync(state.User, services, cancellationToken);
    public static async Task<bool> IsValidAsync(ClaimsPrincipal principal, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (principal.Identity?.IsAuthenticated != true) return true;
        if (!TryAuthority(principal, out var id, out var generation, out var epoch)) return false;
        return await services.GetRequiredService<ICredentialAuthority>().IsCurrentAsync(id!, generation, epoch, cancellationToken);
    }
    internal static bool TryAuthority(ClaimsPrincipal principal, out string? id, out long generation, out long epoch)
    {
        id = principal.FindFirstValue("credential_id"); generation = epoch = 0;
        return id is not null && principal.FindFirstValue("credential_kind") == "session" &&
            long.TryParse(principal.FindFirstValue("generation"), NumberStyles.None, CultureInfo.InvariantCulture, out generation) &&
            long.TryParse(principal.FindFirstValue("account_epoch"), NumberStyles.None, CultureInfo.InvariantCulture, out epoch);
    }
}

/// <summary>Holds a circuit permit, and rejects stale authority before dispatching any browser activity.</summary>
public sealed class SessionCircuitHandler(AuthenticationStateProvider authentication, IServiceProvider services,
    IAdmissionController admission, IOptionsMonitor<SecondBrainOptions> options, TimeProvider clock) : CircuitHandler, IDisposable
{
    private IDisposable? reservation;
    private bool wasAuthenticated;
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        if (state.User.Identity?.IsAuthenticated != true) return;
        wasAuthenticated = true;
        if (!await SessionRevalidationProvider.IsValidAsync(state.User, services, cancellationToken)) throw new AuthorityChangedException();
        var decision = admission.TryReserve(state.User.FindFirstValue("credential_id")!, new(AdmissionResource.Circuit));
        if (!decision.Accepted) throw new InvalidOperationException("The credential circuit limit is exhausted.");
        reservation = decision.Reservation;
    }
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            var principal = (await authentication.GetAuthenticationStateAsync()).User;
            if (wasAuthenticated)
            {
                if (!SessionRevalidationProvider.TryAuthority(principal, out var id, out var generation, out var epoch) ||
                    !await services.GetRequiredService<IAuthRepository>().TouchAsync(id!, generation, epoch, clock.GetUtcNow(),
                        clock.GetUtcNow().AddHours(options.CurrentValue.Auth.SessionIdleHours))) throw new AuthorityChangedException();
            }
            await next(context);
        };
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken) { Dispose(); return Task.CompletedTask; }
    public void Dispose() => Interlocked.Exchange(ref reservation, null)?.Dispose();
}
