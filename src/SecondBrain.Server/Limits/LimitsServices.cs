using Microsoft.Extensions.DependencyInjection.Extensions;

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
