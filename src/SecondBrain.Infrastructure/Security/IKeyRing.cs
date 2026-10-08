using Microsoft.AspNetCore.DataProtection;
using SecondBrain.Core.Security;

namespace SecondBrain.Infrastructure.Security;

/// <summary>
/// Provides versioned HMAC keys and Data Protection envelopes from the writable
/// key ring; see spec §§15.9 (SEC-23), 15.12 (SEC-32) and M0 build plan §5.
/// </summary>
public interface IKeyRing : IHmacKeyRing
{
    /// <summary>Gets the provider for purpose-separated Data Protection envelopes (§15.12).</summary>
    IDataProtectionProvider DataProtectionProvider { get; }
}
