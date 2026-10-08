namespace SecondBrain.Core.Storage;

/// <summary>Creates and migrates both stores, then atomically seeds the first account and admin credential.</summary>
public interface IStoreInitializer
{
    /// <summary>Uses already computed hashes; a repeat call preserves the existing account and credentials.</summary>
    ValueTask<StoreInitializationResult> InitializeAsync(StoreInitializationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The encoded password hash and algorithm parameter version produced by the authentication lane.</summary>
public sealed record PasswordHashRecord(string Hash, int Version);

/// <summary>Caller-computed bootstrap values. Verifier is an HMAC-SHA-256 value, persisted as base64 TEXT.</summary>
public sealed record StoreInitializationRequest(
    string CredentialId,
    byte[] Verifier,
    string Kid,
    IReadOnlyCollection<string> Scopes,
    PasswordHashRecord Password,
    string CredentialName = "initial admin",
    string TimeZone = "UTC");

/// <summary>Whether bootstrap values were inserted and the persisted initial admin credential id.</summary>
public sealed record StoreInitializationResult(bool Initialized, string CredentialId);
