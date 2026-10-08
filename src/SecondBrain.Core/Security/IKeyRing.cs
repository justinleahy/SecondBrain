using Microsoft.AspNetCore.DataProtection;

namespace SecondBrain.Core.Security;

/// <summary>
/// Provides versioned HMAC keys and Data Protection envelopes from the writable
/// key ring; see spec §§15.9 (SEC-23), 15.12 (SEC-32) and M0 build plan §5.
/// </summary>
public interface IKeyRing
{
    /// <summary>Gets the kid used to sign new verifiers; routine rotation preserves old kids (§15.9).</summary>
    string ActiveKid { get; }

    /// <summary>Gets the provider for purpose-separated Data Protection envelopes (§15.12).</summary>
    IDataProtectionProvider DataProtectionProvider { get; }

    /// <summary>Computes an HMAC-SHA-256 verifier using the specified retained kid (§15.3, SEC-6).</summary>
    byte[] Sign(string kid, ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Checks the verifier against every retained kid with constant-time comparisons;
    /// adding a key does not invalidate verifiers created with older keys (§15.9).
    /// </summary>
    bool Verify(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> signature);
}
