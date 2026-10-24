using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Core.Sources;
using SecondBrain.Infrastructure.Extraction;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Infrastructure.Security;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Chat;
using SecondBrain.Server.Components;
using SecondBrain.Server.Ingestion;
using SecondBrain.Server.Limits;
using SecondBrain.Server.Sources;

namespace SecondBrain.Server.Http;

public static class HttpServices
{
    public static IServiceCollection AddLaneDHttp(this IServiceCollection services)
    {
        services.AddOptions<SecondBrainOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ChatService>();
        services.AddHostedService(provider => provider.GetRequiredService<ChatService>());
        services.AddOpenApi();
        services.TryAddSingleton<IPayloadProtection, KeyRingPayloadProtector>();
        services.TryAddSingleton<IExtractorClient>(provider => new DescriptorExtractorClient(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.Extractor.SocketPath));
        services.TryAddSingleton<IManagedDocumentFiles>(provider => new ManagedDocumentFiles(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.DataRoot, provider.GetRequiredService<IMutationJournal>()));
        services.TryAddSingleton<InputStager>(provider => new InputStager(Path.Combine(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.DataRoot, "staging")));
        services.TryAddSingleton<IInputStager>(provider => provider.GetRequiredService<InputStager>());
        services.TryAddSingleton<IStagingRecovery>(provider => provider.GetRequiredService<InputStager>());
        services.TryAddSingleton<IInboxClaims, InboxClaims>();
        services.TryAddSingleton<IFolderSourceFiles, FolderSourceFiles>();
        services.TryAddSingleton<FolderProcessingOrchestrator>();
        services.AddHostedService(provider => provider.GetRequiredService<FolderProcessingOrchestrator>());
        services.TryAddSingleton<EmbeddingProcessor>();
        services.TryAddSingleton<SearchReranker>();
        services.TryAddSingleton<EmbeddingOrchestrator>();
        services.AddHostedService(provider => provider.GetRequiredService<EmbeddingOrchestrator>());
        services.TryAddSingleton<FileProcessingOrchestrator>();
        services.AddHostedService(provider => provider.GetRequiredService<FileProcessingOrchestrator>());
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
        app.UseMiddleware<PasswordRequestMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<RequestAuthorizationMiddleware>();
        app.UseMiddleware<AdmissionMiddleware>();
        app.UseAntiforgery();
        app.UseEndpoints(endpoints =>
        {
            AuthEndpoints.Map(endpoints);
            SourceEndpoints.Map(endpoints);
            KnowledgeEndpoints.Map(endpoints);
            AdministrationEndpoints.Map(endpoints);
            endpoints.MapGet("/ready", ReadinessService.RespondAsync).WithName("readiness");
            endpoints.MapOpenApi("/v1/openapi.json");
            endpoints.MapStaticAssets();
            endpoints.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        });
        next(app);
    };
}
