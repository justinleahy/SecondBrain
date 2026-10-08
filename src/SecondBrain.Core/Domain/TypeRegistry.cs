using System.Collections.Frozen;
using System.Text.Json;
using Json.Schema;

namespace SecondBrain.Core.Domain;

/// <summary>Validates effective props and derives them losslessly from preserved originals.</summary>
public sealed class TypeRegistry
{
    public static IReadOnlySet<string> ReservedBaseFields { get; } = new[]
    {
        "id", "type", "title", "created", "updated", "tags", "aliases", "source", "source_url", "occurred_at",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly FrozenDictionary<string, ContentTypeDefinition> _types;

    public TypeRegistry() : this(BuiltInTypes()) { }

    /// <summary>Builds a complete registry snapshot, suitable for replacement on configuration reload.</summary>
    public TypeRegistry(IEnumerable<ContentTypeDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _types = definitions.ToFrozenDictionary(definition => definition.Key, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<ContentTypeDefinition> Types => _types.Values;

    public ContentTypeDefinition Get(string type) => _types.TryGetValue(type, out var definition)
        ? definition
        : throw new KeyNotFoundException($"The content type '{type}' is not registered.");

    public bool TryGet(string type, out ContentTypeDefinition? definition) => _types.TryGetValue(type, out definition);

    /// <summary>A removed custom type retains its authored type/props while processing falls back to file (§5.4).</summary>
    public ContentTypeDefinition GetProcessingType(string type) => _types.TryGetValue(type, out var definition)
        ? Get(definition.ProcessingType)
        : Get("file");

    public PropertyValidationResult ValidateProperties(string type, JsonElement props)
    {
        var definition = Get(type);
        var errors = new List<PropertyValidationError>();
        if (props.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new PropertyValidationError("", "type", "Properties must be a JSON object."));
            return new PropertyValidationResult(false, errors.AsReadOnly());
        }

        foreach (var property in props.EnumerateObject().Where(property => ReservedBaseFields.Contains(property.Name)))
        {
            errors.Add(new PropertyValidationError($"/{property.Name}", "reserved-field", "Reserved base fields cannot appear inside props."));
        }

        var result = definition.Schema.Evaluate(props, new EvaluationOptions
        {
            RequireFormatValidation = true,
            OutputFormat = OutputFormat.List,
        });
        CollectErrors(result, errors);
        return new PropertyValidationResult(result.IsValid && errors.Count == 0, errors.AsReadOnly());
    }

    /// <summary>
    /// Projects only fields owned by the selected schema. Unknown original values are retained by the aggregate;
    /// malformed known fields and reserved fields are rejected rather than silently dropped.
    /// </summary>
    public JsonElement DeriveEffectiveProperties(string type, JsonElement propsOriginal)
    {
        var definition = Get(type);
        if (propsOriginal.ValueKind != JsonValueKind.Object)
        {
            throw new PropertyValidationException(new PropertyValidationResult(false,
                [new PropertyValidationError("", "type", "Properties must be a JSON object.")]));
        }

        var reserved = propsOriginal.EnumerateObject().Where(property => ReservedBaseFields.Contains(property.Name)).ToArray();
        if (reserved.Length != 0)
        {
            throw new PropertyValidationException(new PropertyValidationResult(false,
                reserved.Select(property => new PropertyValidationError($"/{property.Name}", "reserved-field",
                    "Reserved base fields cannot appear inside props.")).ToArray()));
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in propsOriginal.EnumerateObject().Where(property => definition.PropertyNames.Contains(property.Name)))
            {
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        var effective = document.RootElement.Clone();
        var validation = ValidateProperties(type, effective);
        if (!validation.IsValid)
        {
            throw new PropertyValidationException(validation);
        }
        return effective;
    }

    private static void CollectErrors(EvaluationResults result, List<PropertyValidationError> errors)
    {
        if (result.Errors is not null)
        {
            errors.AddRange(result.Errors.Select(error => new PropertyValidationError(result.InstanceLocation.ToString(), error.Key, error.Value)));
        }
        if (result.Details is not null)
        {
            foreach (var detail in result.Details)
            {
                CollectErrors(detail, errors);
            }
        }
    }

    private static IEnumerable<ContentTypeDefinition> BuiltInTypes()
    {
        yield return new ContentTypeDefinition("note", 1,
            """{ "$id": "brain:type/note/1", "type": "object", "properties": {}, "additionalProperties": false }""", "note");
        yield return new ContentTypeDefinition("article", 1, """
            { "$id": "brain:type/article/1", "type": "object",
              "properties": {
                "author": { "type": "string" },
                "published_at": { "type": "string", "format": "date-time" },
                "url": { "type": "string", "format": "uri" },
                "site": { "type": "string" }
              },
              "additionalProperties": false,
              "x-occurred-basis": "published_at", "x-index": ["author", "site"], "x-people": { "author": "author" } }
            """, "article");
        yield return new ContentTypeDefinition("file", 1,
            """{ "$id": "brain:type/file/1", "type": "object", "properties": {}, "additionalProperties": false }""", "file");
    }
}

public sealed record PropertyValidationError(string PropertyPath, string Keyword, string Message);
public sealed record PropertyValidationResult(bool IsValid, IReadOnlyList<PropertyValidationError> Errors);

public sealed class PropertyValidationException : ArgumentException
{
    public PropertyValidationResult Validation { get; }

    public PropertyValidationException(PropertyValidationResult validation)
        : base("The document properties do not satisfy the selected type schema.", "props")
    {
        Validation = validation;
    }
}
