using SecondBrain.Extraction.Contracts;

namespace SecondBrain.Core.Extraction;

public interface IExtractorClient
{
    ValueTask<ExtractionResult> ExtractAsync(Stream stagedInput, string formatHint, CancellationToken cancellationToken = default);
}
