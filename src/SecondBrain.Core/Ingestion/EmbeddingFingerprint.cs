using System.Security.Cryptography;
using System.Text;

namespace SecondBrain.Core.Ingestion;

public static class EmbeddingFingerprint
{
    public static string Create(string provider, string? endpoint, string model, int dimensions) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', provider, endpoint,
            model, dimensions, "unit-f32", "context-newline-text-v1", TextProcessor.Generation))));
}
