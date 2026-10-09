using Microsoft.Extensions.DependencyInjection.Extensions;
using SecondBrain.Core.Limits;
using SecondBrain.Infrastructure.FileSystem;
using SecondBrain.Storage;
using SecondBrain.Server.Http;

namespace SecondBrain.Server.Composition;

/// <summary>Lane A composition: stores and durability (spec §5.1, §8, §15.11).</summary>
public static class LaneAStorage
{
    /// <summary>Registers stores, migration/init services, publication/journal recovery, and storage readiness.</summary>
    public static IServiceCollection AddStorage(this IServiceCollection services)
    {
        // Migration preflight and admission share one mount-aware probe of the data root's filesystem.
        services.TryAddSingleton<IDiskCapacity, DiskCapacity>();
        services.AddSecondBrainStorage();
        services.AddSingleton<IReadinessContributor, MigrationReadinessContributor>();
        return services;
    }
}
