using System.Net;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Providers;

namespace SecondBrain.Core.Privacy;

/// <summary>The provider-data egress checks a provider adapter applies to each endpoint, request and connection.</summary>
public interface IProviderEgressPolicy
{
    /// <summary>Validates the writing request and exact connected address, then initiates the socket write
    /// while holding the same boundary used to publish accepted policy replacements. Never await inside initiation.</summary>
    ValueTask AdmitWrite(ModelRole role, IProviderBinding binding, Uri requestUri, IPAddress connectedAddress,
        bool discovery, Func<ValueTask> initiateWrite);
    bool IsLocalEndpoint(Uri? endpoint);
    void ValidateRequest(ModelRole role, IProviderBinding binding, Uri requestUri, bool discovery = false);
    void VerifyResolvedAddresses(Uri endpoint, IReadOnlyList<IPAddress> addresses);
    void VerifyConnectionAddresses(string host, int port, IReadOnlyList<IPAddress> addresses);
    IDisposable RegisterConfigurationValidator(Action<SecondBrainOptions> validator);
}
