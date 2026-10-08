namespace SecondBrain.Core.Privacy;

/// <summary>The replaceable TCP operation, allowing deterministic timeout tests.</summary>
public interface ICanaryConnector
{
    ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken);
}
