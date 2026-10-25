using System.Collections.Concurrent;
using System.Text;
using SecondBrain.Core.Extraction;
using SecondBrain.Extraction.Contracts;

namespace SecondBrain.Server.Tests.Support;

/// <summary>Fails the first three attempts for two synthetic browser files, then uses the real extractor.</summary>
internal sealed class TransientFixtureExtractor(IExtractorClient inner) : IExtractorClient
{
    private readonly ConcurrentDictionary<string, int> attempts = new();
    public async ValueTask<ExtractionResult> ExtractAsync(Stream stagedInput, string formatHint, CancellationToken cancellationToken = default)
    {
        if (formatHint == ".md" && stagedInput.CanSeek)
        {
            var position = stagedInput.Position;
            var buffer = new byte[128];
            var length = await stagedInput.ReadAsync(buffer, cancellationToken);
            stagedInput.Position = position;
            var prefix = Encoding.UTF8.GetString(buffer, 0, length);
            foreach (var marker in new[] { "M3_TRANSIENT_1280", "M3_TRANSIENT_390" })
                if (prefix.Contains(marker, StringComparison.Ordinal) && attempts.AddOrUpdate(marker, 1, (_, count) => count + 1) <= 3)
                    throw new IOException("Synthetic transient extractor outage.");
        }
        return await inner.ExtractAsync(stagedInput, formatHint, cancellationToken);
    }
}
