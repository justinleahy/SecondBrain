using System.Net;
using SecondBrain.Core.Privacy;

namespace SecondBrain.Infrastructure.Network;

/// <summary>System DNS; literal addresses do not require control-plane DNS egress.</summary>
public sealed class SystemDnsResolver : IDnsResolver
{
    public async ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            return [address];
        }

        // DNS and the configured Access certificate endpoint are control-plane allowances.
        // Neither grants permission for provider data or changes trusted-service locality.
        return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
    }
}
