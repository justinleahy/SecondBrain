using System.Net;

namespace SecondBrain.Core.Privacy;

/// <summary>Resolves trusted hosts for configuration-time pins and transport checks.</summary>
public interface IDnsResolver
{
    ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default);
}
