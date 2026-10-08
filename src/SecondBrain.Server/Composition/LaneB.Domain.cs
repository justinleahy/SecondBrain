using Microsoft.Extensions.DependencyInjection.Extensions;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Domain;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;
using SecondBrain.Providers.OpenAICompatible;

namespace SecondBrain.Server.Composition;

/// <summary>Lane B composition: domain, bindings and privacy (spec §5, §13, §15.6).</summary>
public static class LaneBDomain
{
    /// <summary>Registers domain services; implemented in M0 item 5.</summary>
    public static IServiceCollection AddDomain(this IServiceCollection services)
    {
        services.TryAddSingleton(_ => new TypeRegistry());
        return services;
    }

    /// <summary>Registers provider services; implemented in M0 item 7.</summary>
    public static IServiceCollection AddProviders(this IServiceCollection services)
    {
        services.TryAddSingleton<ModelCatalog>();
        services.TryAddSingleton<IProviderCredentialResolver, EnvironmentProviderCredentialResolver>();
        services.TryAddSingleton<IPolicyHttpClientFactory, PolicyHttpClientFactory>();
        services.TryAddSingleton<IProviderRegistry, ProviderRegistry>();
        services.AddHostedService<ProviderStartup>();
        return services;
    }

    /// <summary>Registers privacy services; implemented in M0 item 8.</summary>
    public static IServiceCollection AddPrivacy(this IServiceCollection services)
    {
        services.AddOptions<SecondBrainOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDnsResolver, SystemDnsResolver>();
        services.TryAddSingleton<ICanaryConnector, TcpCanaryConnector>();
        services.TryAddSingleton<PrivacyPolicy>();
        services.TryAddSingleton<IPrivacyPolicy>(provider => provider.GetRequiredService<PrivacyPolicy>());
        services.TryAddSingleton<IPrivacyReadiness>(provider => provider.GetRequiredService<PrivacyPolicy>());
        services.TryAddSingleton<EgressCanary>();
        services.AddHostedService<PrivacyCanaryHost>();
        return services;
    }

    private sealed class ProviderStartup(IProviderRegistry registry) : IHostedService
    {
        // Materializing the registry rejects unresolved roles/capabilities/limits at startup.
        public Task StartAsync(CancellationToken cancellationToken) { _ = registry.ProviderNames; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class PrivacyCanaryHost(EgressCanary canary, TimeProvider timeProvider) : BackgroundService
    {
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            // Await the startup check before the daemon begins serving provider requests.
            await canary.RunOnceAsync(cancellationToken);
            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(EgressCanary.Interval, timeProvider, stoppingToken);
                await canary.RunOnceAsync(stoppingToken);
            }
        }
    }
}
