namespace SecondBrain.Storage;

/// <summary>
/// Handle to the disposable index store and its committed metadata snapshots;
/// publication across stores uses the coordinator (spec §5.1, §8, Appendix A.2).
/// </summary>
public interface IIndexStore : IStoreHandle
{
}
