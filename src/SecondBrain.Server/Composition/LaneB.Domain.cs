namespace SecondBrain.Server.Composition;

/// <summary>Lane B composition: domain, bindings and privacy (spec §5, §13, §15.6).</summary>
public static class LaneBDomain
{
    /// <summary>Registers domain services; implemented in M0 item 5.</summary>
    public static IServiceCollection AddDomain(this IServiceCollection services) => services;

    /// <summary>Registers provider services; implemented in M0 item 7.</summary>
    public static IServiceCollection AddProviders(this IServiceCollection services) => services;

    /// <summary>Registers privacy services; implemented in M0 item 8.</summary>
    public static IServiceCollection AddPrivacy(this IServiceCollection services) => services;
}
