namespace SecondBrain.Core.Security;

/// <summary>Lane A binds this hook to its durable account epoch transaction.</summary>
public interface IAccountEpochRevoker
{
    ValueTask BumpEpochAsync(CancellationToken cancellationToken = default);
}
