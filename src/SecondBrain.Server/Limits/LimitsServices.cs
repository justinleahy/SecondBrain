using Microsoft.Extensions.DependencyInjection.Extensions;
using SecondBrain.Core.Limits;
using SecondBrain.Infrastructure.FileSystem;

namespace SecondBrain.Server.Limits;

public static class LimitsServices
{
    public static IServiceCollection AddLaneDLimits(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDiskCapacity, DiskCapacity>();
        services.TryAddSingleton<IAdmissionController, AdmissionController>();
        return services;
    }
}
