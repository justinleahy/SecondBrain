using System.Security.Cryptography;
using System.Text;

namespace SecondBrain.Core.Ingestion;

public static class EmbeddingFingerprint
{
    public static string Create(string provider, string? endpoint, string model, int dimensions, ChunkingProfile? profile = null)
    {
        var binding = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', provider, endpoint,
            model, dimensions, "unit-f32", "context-newline-text-v1", TextProcessor.Generation))));
        return profile is null || profile == new ChunkingProfile() ? binding : binding + ":" +
            FormattableString.Invariant($"{profile.MaxTokens}-{profile.TargetTokens}-{profile.OverlapTokens}");
    }
    /// <summary>The existing provider identity remains independently verifiable after rechunking.</summary>
    public static string BindingPart(string fingerprint) => fingerprint.Split(':', 2)[0];
    public static ChunkingProfile Profile(string fingerprint)
    {
        var parts = fingerprint.Split(':', 2);
        if (parts.Length == 1) return new();
        var bounds = parts[1].Split('-');
        if (bounds.Length != 3 || !int.TryParse(bounds[0], out var maximum) || !int.TryParse(bounds[1], out var target) || !int.TryParse(bounds[2], out var overlap)
            || maximum is < 32 or > 8192 || target < 16 || target > maximum || overlap < 0 || overlap >= target)
            throw new ArgumentException("Invalid embedding chunk profile.");
        return new(target, maximum, overlap);
    }
    public static long ChunkGeneration(string fingerprint)
    {
        var profile = Profile(fingerprint);
        return profile == new ChunkingProfile() ? 1 : ((long)profile.MaxTokens << 28) | ((long)profile.TargetTokens << 14) | (uint)profile.OverlapTokens;
    }
}
