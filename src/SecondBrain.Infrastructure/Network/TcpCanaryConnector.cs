using System.Net.Sockets;
using SecondBrain.Core.Privacy;

namespace SecondBrain.Infrastructure.Network;

/// <summary>Connects without sending application data, proxies or HTTP redirects.</summary>
public sealed class TcpCanaryConnector : ICanaryConnector
{
    public async ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
    }
}
