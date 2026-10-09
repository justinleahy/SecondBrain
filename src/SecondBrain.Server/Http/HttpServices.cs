using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace SecondBrain.Server.Http;

public static class HttpServices
{
    public static IServiceCollection AddLaneDHttp(this IServiceCollection services)
    {
        services.AddOptions<Core.Configuration.SecondBrainOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddOpenApi();
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        services.AddSingleton<IDataProtectionProvider, KeyRingDataProtectionProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<KestrelServerOptions>, ConfiguredKestrelOptions>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, LaneDStartupFilter>());
        services.TryAddSingleton<ReadinessService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReadinessContributor, StoreReadinessContributor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReadinessContributor, CanaryReadinessContributor>());
        services.TryAddSingleton<IAccessJwksTransport, AccessJwksTransport>();
        services.TryAddSingleton<AccessJwksCache>();
        services.AddHostedService<AccessJwksRefreshService>();
        services.AddAuthentication().AddJwtBearer(AccessAuthentication.Scheme, _ => { });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<JwtBearerOptions>, AccessJwtBearerOptions>());
        return services;
    }
}

public sealed class LaneDStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var environment = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
        if (environment.IsEnvironment("Testing"))
            StaticWebAssetsLoader.UseStaticWebAssets(environment, app.ApplicationServices.GetRequiredService<IConfiguration>());
        app.UseMiddleware<ProblemMiddleware>();
        app.UseMiddleware<TrustedForwardedHeadersMiddleware>();
        app.UseMiddleware<RequestPolicyMiddleware>();
        app.UseMiddleware<AccessGateMiddleware>();
        app.UseRouting();
        // Endpoint metadata adds anonymous-browser Origin requirements after routing.
        app.UseMiddleware<RequestPolicyMiddleware>();
        // Password bodies are admitted and bounded after Origin/Access and before authentication's antiforgery read.
        app.UseMiddleware<Auth.PasswordRequestMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<Auth.RequestAuthorizationMiddleware>();
        app.UseMiddleware<Limits.AdmissionMiddleware>();
        app.UseAntiforgery();
        app.UseEndpoints(endpoints =>
        {
            Auth.AuthEndpoints.Map(endpoints);
            Sources.SourceEndpoints.Map(endpoints);
            AdministrationEndpoints.Map(endpoints);
            endpoints.MapGet("/ready", ReadinessService.RespondAsync).WithName("readiness");
            endpoints.MapOpenApi("/v1/openapi.json");
            endpoints.MapStaticAssets();
            endpoints.MapRazorComponents<Components.App>().AddInteractiveServerRenderMode();
        });
        next(app);
    };
}
