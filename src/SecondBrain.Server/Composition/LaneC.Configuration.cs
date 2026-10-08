namespace SecondBrain.Server.Composition;

/// <summary>Lane C composition: configuration and keys (spec §14, §15.9).</summary>
public static class LaneCConfiguration
{
    /// <summary>Registers configuration services; implemented in M0 item 2.</summary>
    public static IServiceCollection AddConfiguration(this IServiceCollection services) => services;

    /// <summary>Registers key-ring services; implemented in M0 item 3a.</summary>
    public static IServiceCollection AddKeyRing(this IServiceCollection services) => services;
}
