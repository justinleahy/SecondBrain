using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SecondBrain.Server.Sources;

public static class SourcesServices
{
    public static IServiceCollection AddLaneDSources(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISourcePathValidator, SourcePathValidator>();
        services.TryAddSingleton<ISourceRepository>(provider => ActivatorUtilities.CreateInstance<SqliteSourceRepository>(provider));
        return services;
    }
}
