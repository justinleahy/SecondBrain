using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Storage;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Http;
using SecondBrain.Server.Limits;
using SecondBrain.Server.Sources;
using SecondBrain.Storage.Auth;

namespace SecondBrain.Server.Composition;

/// <summary>Lane D composition: HTTP, authentication and admission (spec §7.3, §15.2–§15.8).</summary>
public static class LaneDHttp
{
    public static IServiceCollection AddHttpHost(this IServiceCollection services) => HttpServices.AddLaneDHttp(services);
    public static IServiceCollection AddAuth(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider => PasswordPolicy.Load(new SqlitePasswordPolicyStore(provider.GetRequiredService<IStateStore>())));
        services.TryAddSingleton<IPasswordHasher, PasswordHasher>();
        services.TryAddSingleton(provider => ActivatorUtilities.CreateInstance<CredentialFactory>(provider));
        services.TryAddSingleton<IAdminCredentialFactory>(provider => provider.GetRequiredService<CredentialFactory>());
        services.TryAddSingleton<IAuthRepository>(provider => ActivatorUtilities.CreateInstance<AuthRepository>(provider));
        services.TryAddSingleton<ICredentialAuthority>(provider => ActivatorUtilities.CreateInstance<CredentialAuthority>(provider));
        services.TryAddSingleton(provider => ActivatorUtilities.CreateInstance<CredentialService>(provider));
        services.TryAddSingleton(provider => ActivatorUtilities.CreateInstance<LoginService>(provider));
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = CredentialAuthenticationHandler.SchemeName;
            options.DefaultChallengeScheme = CredentialAuthenticationHandler.SchemeName;
        }).AddScheme<AuthenticationSchemeOptions, CredentialAuthenticationHandler>(CredentialAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization();
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, SessionRevalidationProvider>();
        services.AddScoped<CircuitHandler, SessionCircuitHandler>();
        return services;
    }
    public static IServiceCollection AddLimits(this IServiceCollection services)
    {
        services.TryAddSingleton<IScopePolicy, M0ScopePolicy>();
        services.AddLaneDLimits();
        services.AddLaneDSources();
        return services;
    }
}
