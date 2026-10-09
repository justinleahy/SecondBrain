using Microsoft.Extensions.DependencyInjection.Extensions;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Domain;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;
using SecondBrain.Infrastructure.Configuration;
using SecondBrain.Infrastructure.Network;
using SecondBrain.Providers.OpenAICompatible;
using SecondBrain.Server.Http;

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
        services.TryAddSingleton<IProviderCredentialResolver, ConfigurationProviderCredentialResolver>();
        services.TryAddSingleton<IPolicyHttpClientFactory, PolicyHttpClientFactory>();
        services.TryAddSingleton<IProviderRegistry, ProviderRegistry>();
        services.AddHostedService<ProviderStartup>();
        services.AddSingleton<ProviderReadinessProbe>();
        foreach (var role in new[] { ModelRole.Chat, ModelRole.Enrich, ModelRole.Embed })
            services.AddSingleton<IReadinessContributor>(provider => new ProviderReadinessContributor(role, provider.GetRequiredService<ProviderReadinessProbe>()));
        services.AddSingleton<IReadinessContributor>(provider => new OptionalRerankReadinessContributor(provider));
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
        services.TryAddSingleton<IProviderEgressPolicy>(provider => provider.GetRequiredService<PrivacyPolicy>());
        services.TryAddSingleton<EgressCanary>();
        services.AddHostedService<PrivacyCanaryHost>();
        return services;
    }

    private sealed class ProviderStartup(IProviderRegistry registry, PrivacyPolicy privacy, ModelCatalog catalog,
        IProviderCredentialResolver credentials, ReloadingConfiguration configuration) : IHostedService
    {
        // Materializing the registry rejects unresolved roles/capabilities/limits at startup.
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = registry.ProviderNames;
            configuration.Validating += Validate;
            return Task.CompletedTask;
        }
        private void Validate(SecondBrainOptions candidate)
        {
            ProviderRegistry.ValidateConfiguration(candidate, catalog, credentials);
            privacy.ValidateConfiguration(candidate);
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            configuration.Validating -= Validate;
            return Task.CompletedTask;
        }
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

internal sealed class ConfigurationProviderCredentialResolver(ISecretResolver secrets) : IProviderCredentialResolver
{
    public string? Resolve(string? reference) => reference is null ? null : secrets.Resolve(reference);
}

internal sealed class OptionalRerankReadinessContributor(IServiceProvider services) : IReadinessContributor
{
    public string Name => "provider:rerank";
    public ValueTask<ReadinessStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<SecondBrainOptions>>();
        return options.CurrentValue.Models.Rerank is null
            ? ValueTask.FromResult(new ReadinessStatus(true, "Optional rerank role is not configured.", services.GetRequiredService<TimeProvider>().GetUtcNow()))
            : services.GetRequiredService<ProviderReadinessProbe>().CheckAsync(ModelRole.Rerank, cancellationToken);
    }
}
