namespace SecondBrain.Core.Ingestion;

/// <summary>The persisted and API-visible document status vocabulary (spec Appendix A <c>documents.status</c>).</summary>
public static class DocumentStatusNames
{
    public const string Missing = "missing";
    public const string RemovedFromSource = "removed_from_source";
    public const string Deleted = "deleted";
    public const string Purged = "purged";

    /// <summary>Statuses whose document is no longer served: not readable, searchable or editable.</summary>
    public static IReadOnlyList<string> Unavailable { get; } = [Deleted, Purged, RemovedFromSource, Missing];

    public static bool IsUnavailable(string status) => status is Deleted or Purged or RemovedFromSource or Missing;
}
