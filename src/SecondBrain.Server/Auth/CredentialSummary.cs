using SecondBrain.Core.Auth;

namespace SecondBrain.Server.Auth;

public sealed record CredentialSummary(string Id, string Name, string Kind, string Scopes, long Generation, string? Device,
    string CreatedAt, string? LastUsedAt, string? ExpiresAt, string? IdleExpiresAt, string? AbsoluteExpiresAt, string? RevokedAt)
{
    public static CredentialSummary From(CredentialRecord value) => new(value.Id, value.Name, value.Kind, value.Scopes,
        value.Generation, value.Device, value.CreatedAt, value.LastUsedAt, value.ExpiresAt, value.IdleExpiresAt, value.AbsoluteExpiresAt, value.RevokedAt);
}
