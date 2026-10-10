namespace SecondBrain.Core.Ingestion;

/// <summary>
/// Deterministic staging-policy rejections of one source entry (size, links, type), judged on its stat alone
/// before any content is read. The stat evidence lets a rejection stand until the entry itself changes (ING-7).
/// </summary>
public static class SourceEntryPolicy
{
    private const string LengthKey = "source_entry_length";
    private const string ModifiedKey = "source_entry_modified_at";

    public static InvalidDataException Rejected(string reason, long length, DateTimeOffset modifiedAt)
    {
        var exception = new InvalidDataException(reason);
        exception.Data[LengthKey] = length;
        exception.Data[ModifiedKey] = modifiedAt;
        return exception;
    }

    public static bool TryGetEvidence(InvalidDataException exception, out long length, out DateTimeOffset modifiedAt)
    {
        length = exception.Data[LengthKey] is long recordedLength ? recordedLength : 0;
        modifiedAt = exception.Data[ModifiedKey] is DateTimeOffset recordedModified ? recordedModified : default;
        return exception.Data[LengthKey] is long && exception.Data[ModifiedKey] is DateTimeOffset;
    }

    /// <summary>A bounded reason code for durable state; free-form messages never leave the process.</summary>
    public static string ReasonOf(InvalidDataException exception) => ReasonOf(exception.Message);

    public static string ReasonOf(string reason) =>
        reason is { Length: > 0 and <= 64 } && char.IsAsciiLetterLower(reason[0]) && reason.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')
            ? reason : "source_rejected";
}

/// <summary>The durable outcome of one rejected source entry: its path, named reason and stat evidence.</summary>
public sealed record SourceEntryRejection(string RelativePath, string Reason, long Length, DateTimeOffset ModifiedAt);
