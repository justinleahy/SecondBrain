using SecondBrain.Core.Ingestion;

namespace SecondBrain.Storage.Ingestion;

/// <summary>SQL renderings of the Core document status vocabulary, so statements never restate it.</summary>
internal static class DocumentStatusSql
{
    /// <summary>A comma-separated literal list for <c>status NOT IN (...)</c>.</summary>
    internal static readonly string Unavailable = string.Join(',', DocumentStatusNames.Unavailable.Select(status => $"'{status}'"));
}
