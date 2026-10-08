using System.Collections.Frozen;
using System.Text.Json;
using Json.Schema;

namespace SecondBrain.Core.Domain;

/// <summary>A versioned property schema and its processing annotations (spec §5.4, Appendix E).</summary>
public sealed class ContentTypeDefinition
{
    internal JsonSchema Schema { get; }

    public string Key { get; }
    public int SchemaVersion { get; }
    public string ProcessingType { get; }
    public JsonElement SchemaDocument { get; }
    public IReadOnlySet<string> PropertyNames { get; }
    public TypeEngineMetadata Metadata { get; }

    public ContentTypeDefinition(string key, int schemaVersion, string schemaJson, string processingType = "file")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(processingType);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        using var document = JsonDocument.Parse(schemaJson);
        SchemaDocument = document.RootElement.Clone();
        if (SchemaDocument.ValueKind != JsonValueKind.Object ||
            !SchemaDocument.TryGetProperty("type", out var type) || type.GetString() != "object" ||
            !SchemaDocument.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False)
        {
            throw new ArgumentException("A type schema must be an object schema with additionalProperties: false.", nameof(schemaJson));
        }

        var properties = SchemaDocument.TryGetProperty("properties", out var propertySchemas)
            ? propertySchemas.EnumerateObject().Select(property => property.Name).ToFrozenSet(StringComparer.Ordinal)
            : Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
        if (properties.Any(TypeRegistry.ReservedBaseFields.Contains))
        {
            throw new ArgumentException("A type schema cannot declare a reserved base field inside props.", nameof(schemaJson));
        }

        Key = key;
        SchemaVersion = schemaVersion;
        ProcessingType = processingType;
        PropertyNames = properties;
        Metadata = TypeEngineMetadata.Parse(SchemaDocument, properties, key == "note" ? "created" : null);
        // Every schema is compiled in a private registry. The library's global registry forbids re-registering
        // an $id, so using it here would break independent instances and configuration reloads.
        Schema = JsonSchema.FromText(schemaJson, new BuildOptions
        {
            SchemaRegistry = new SchemaRegistry(),
            // Appendix E uses extension annotations, permitted by draft 2020-12. The library's
            // newer default dialect rejects unknown keywords, including these normative x- fields.
            Dialect = Dialect.Draft202012,
        });
    }
}

/// <summary>Engine meaning of the x- annotations, separate from JSON Schema validation.</summary>
public sealed class TypeEngineMetadata
{
    public string? OccurredBasis { get; }
    public IReadOnlySet<string> IndexedProperties { get; }
    public IReadOnlyDictionary<string, string> PeopleRoles { get; }
    public IReadOnlyList<string> NaturalKeyProperties { get; }
    public IReadOnlyDictionary<string, JsonElement> Annotations { get; }

    private TypeEngineMetadata(string? occurredBasis, IEnumerable<string> indexedProperties,
        Dictionary<string, string> peopleRoles, string[] naturalKeyProperties, Dictionary<string, JsonElement> annotations)
    {
        OccurredBasis = occurredBasis;
        IndexedProperties = indexedProperties.ToFrozenSet(StringComparer.Ordinal);
        PeopleRoles = peopleRoles.ToFrozenDictionary(StringComparer.Ordinal);
        NaturalKeyProperties = Array.AsReadOnly(naturalKeyProperties);
        Annotations = annotations.ToFrozenDictionary(StringComparer.Ordinal);
    }

    internal static TypeEngineMetadata Parse(JsonElement schema, IReadOnlySet<string> properties, string? defaultBasis)
    {
        var annotations = schema.EnumerateObject()
            .Where(property => property.Name.StartsWith("x-", StringComparison.Ordinal))
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        var occurredBasis = annotations.TryGetValue("x-occurred-basis", out var basis) ? ReadString(basis) : defaultBasis;
        var indexed = annotations.TryGetValue("x-index", out var index)
            ? index.EnumerateArray().Select(ReadString).ToArray()
            : [];
        var people = annotations.TryGetValue("x-people", out var roles)
            ? roles.EnumerateObject().ToDictionary(property => property.Name, property => ReadString(property.Value), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var naturalKey = annotations.TryGetValue("x-natural-key", out var key)
            ? ReadString(key).Split('+')
            : [];

        var referenced = indexed.Concat(people.Keys).Concat(naturalKey);
        if (occurredBasis is not null and not "created")
        {
            referenced = referenced.Append(occurredBasis);
        }

        if (referenced.Any(property => !properties.Contains(property)))
        {
            throw new ArgumentException("Engine annotations must reference properties declared by the type schema.", nameof(schema));
        }

        return new TypeEngineMetadata(occurredBasis, indexed, people, naturalKey, annotations);
    }

    private static string ReadString(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new ArgumentException("Engine annotations must contain nonempty strings.", nameof(value));
    }
}
