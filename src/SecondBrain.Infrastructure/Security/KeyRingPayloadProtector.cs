using Microsoft.AspNetCore.DataProtection;
using SecondBrain.Core.Retrieval;
using SecondBrain.Core.Security;

namespace SecondBrain.Infrastructure.Security;

/// <summary>Protects opaque retrieval payloads (cursors) with the key ring's Data Protection provider.</summary>
public sealed class KeyRingPayloadProtector(IKeyRing ring) : IPayloadProtection
{
    public string Protect(string purpose, string plaintext) => ring.DataProtectionProvider.CreateProtector(KeyRingPurposes.Cursor, purpose).Protect(plaintext);
    public string Unprotect(string purpose, string protectedText) => ring.DataProtectionProvider.CreateProtector(KeyRingPurposes.Cursor, purpose).Unprotect(protectedText);
}
