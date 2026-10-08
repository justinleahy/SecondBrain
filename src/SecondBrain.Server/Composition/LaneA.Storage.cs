namespace SecondBrain.Server.Composition;

/// <summary>Lane A composition: stores and durability (spec §5.1, §8, §15.11).</summary>
public static class LaneAStorage
{
    /// <summary>Registers lane A services; implemented in M0 items 4 and 6.</summary>
    public static IServiceCollection AddStorage(this IServiceCollection services) => services;
}
