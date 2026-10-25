using SecondBrain.Extraction.Contracts;

namespace SecondBrain.Core.Extraction;

public interface IExtractorClient
{
    ValueTask<ExtractionResult> ExtractAsync(Stream stagedInput, string formatHint, CancellationToken cancellationToken = default);
    ValueTask<ExtractionResult> ExtractCalendarAsync(Stream stagedInput, CalendarWindow window, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This extractor cannot materialize calendars.");
}
