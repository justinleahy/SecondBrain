using System.Globalization;
using System.Text.Json;

namespace SecondBrain.Core.Retrieval;

/// <summary>Rejects malformed client filters before provider work or database access.</summary>
public static class RetrievalFilterValidation
{
    private static readonly HashSet<string> BaseFields = new(["id", "document_id", "title", "type", "status", "revision", "occurred_at", "updated_at"], StringComparer.Ordinal);
    private static readonly HashSet<string> IndexedFields = new(new Domain.TypeRegistry().Types
        .Where(type => type.Key is "note" or "article" or "file")
        .SelectMany(type => type.Metadata.IndexedProperties).Select(name => "props." + name), StringComparer.Ordinal);
    public static void Validate(RetrievalFilters? filters)
    {
        if (filters is null) return;
        if (filters.Type is not null && filters.Type is not ("note" or "article" or "file")) throw new ArgumentException("Unsupported M1 type.");
        if (filters.Tags?.Any(tag => tag is null) == true) throw new ArgumentException("invalid-tag-filter");
        foreach (var person in filters.People ?? [])
            if (person is null || string.IsNullOrWhiteSpace(person.Value)) throw new ArgumentException("invalid-person-filter");
        foreach (var predicate in filters.Predicates ?? [])
        {
            if (predicate is null || string.IsNullOrWhiteSpace(predicate.Field) || string.IsNullOrWhiteSpace(predicate.Op))
                throw new ArgumentException("invalid-predicate");
            if (!BaseFields.Contains(predicate.Field) && !IndexedFields.Contains(predicate.Field)) throw new ArgumentException("unindexed-property");
            if (predicate.Op is not ("exists" or "in" or "eq" or "neq" or "lt" or "lte" or "gt" or "gte")) throw new ArgumentException("Invalid predicate operator.");
            if (predicate.Op == "exists") continue;
            if (predicate.Op == "in")
            {
                if (predicate.Value.ValueKind != JsonValueKind.Array || predicate.Value.GetArrayLength() > 200) throw new ArgumentException("Invalid predicate.");
                foreach (var value in predicate.Value.EnumerateArray()) ValidateScalar(value);
            }
            else ValidateScalar(predicate.Value);
        }
        foreach (var window in new[] { filters.Occurred, filters.Updated })
            foreach (var value in new[] { window?.From, window?.To })
                if (value is not null && !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw new ArgumentException("invalid-time-filter");
    }

    private static void ValidateScalar(JsonElement value)
    {
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            || value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var number) || !double.IsFinite(number)))
            throw new ArgumentException("invalid-predicate-value");
    }
}
