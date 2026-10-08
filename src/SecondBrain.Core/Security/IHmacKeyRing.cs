namespace SecondBrain.Core.Security;

/// <summary>
/// Provides versioned HMAC keys from the writable key ring; see spec §15.9 (SEC-23)
/// and M0 build plan §5.
/// </summary>
public interface IHmacKeyRing
{
    /// <summary>Gets the kid used to sign new verifiers; routine rotation preserves old kids (§15.9).</summary>
    string ActiveKid { get; }

    /// <summary>Computes an HMAC-SHA-256 verifier using the specified retained kid (§15.3, SEC-6).</summary>
    byte[] Sign(string kid, ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Checks the verifier against every retained kid with constant-time comparisons;
    /// adding a key does not invalidate verifiers created with older keys (§15.9).
    /// </summary>
    bool Verify(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> signature);
}
