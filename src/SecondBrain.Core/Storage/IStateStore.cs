namespace SecondBrain.Core.Storage;

/// <summary>
/// Handle to the authoritative state store; it never provides cross-store atomicity
/// with the index (spec §5.1, Appendix A.1 and publication contract §8).
/// </summary>
public interface IStateStore : IStoreHandle
{
}
