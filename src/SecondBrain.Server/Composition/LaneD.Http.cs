namespace SecondBrain.Server.Composition;

/// <summary>Lane D composition: HTTP, authentication and admission (spec §7.3, §15.2–§15.8).</summary>
public static class LaneDHttp
{
    /// <summary>Registers HTTP host services; implemented in M0 item 9.</summary>
    public static IServiceCollection AddHttpHost(this IServiceCollection services) => services;

    /// <summary>Registers authentication services; implemented in M0 items 10–12.</summary>
    public static IServiceCollection AddAuth(this IServiceCollection services) => services;

    /// <summary>Registers scope and limits services; implemented in M0 item 13.</summary>
    public static IServiceCollection AddLimits(this IServiceCollection services) => services;
}
