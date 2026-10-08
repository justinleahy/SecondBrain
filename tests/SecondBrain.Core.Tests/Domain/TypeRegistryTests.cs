using System.Text.Json;
using SecondBrain.Core.Domain;
using Xunit;

namespace SecondBrain.Core.Tests.Domain;

public sealed class TypeRegistryTests
{
    private readonly TypeRegistry _registry = new();

    [Fact]
    public void BuiltInsMatchAppendixE()
    {
        Assert.Equal(["article", "file", "note"], _registry.Types.Select(type => type.Key).Order());
        foreach (var definition in _registry.Types)
        {
            Assert.Equal(1, definition.SchemaVersion);
            Assert.Equal($"brain:type/{definition.Key}/1", definition.SchemaDocument.GetProperty("$id").GetString());
            Assert.Equal(JsonValueKind.False, definition.SchemaDocument.GetProperty("additionalProperties").ValueKind);
        }
    }

    [Theory]
    [InlineData("note", "{}", true)]
    [InlineData("file", "{}", true)]
    [InlineData("article", "{}", true)]
    [InlineData("article", "{\"author\":\"Sarah\",\"site\":\"Example\",\"published_at\":\"2026-10-08T10:00:00-04:00\",\"url\":\"https://example.test/story\"}", true)]
    [InlineData("article", "{\"author\":123}", false)]
    [InlineData("article", "{\"site\":false}", false)]
    [InlineData("article", "{\"published_at\":\"not a date\"}", false)]
    [InlineData("article", "{\"published_at\":\"2026-02-30T10:00:00Z\"}", false)]
    [InlineData("article", "{\"published_at\":\"2026-10-08\"}", false)]
    [InlineData("article", "{\"url\":\"relative/path\"}", false)]
    [InlineData("article", "{\"author\":null}", false)]
    [InlineData("article", "{\"unknown\":\"kept only in originals\"}", false)]
    [InlineData("note", "{\"author\":\"Sarah\"}", false)]
    [InlineData("file", "{\"extra\":{}}", false)]
    [InlineData("note", "[]", false)]
    [InlineData("note", "null", false)]
    public void ValidatesPropertySchemasIncludingFormats(string type, string json, bool expected)
    {
        var result = _registry.ValidateProperties(type, Parse(json));
        Assert.Equal(expected, result.IsValid);
        Assert.Equal(!expected, result.Errors.Count > 0);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("type")]
    [InlineData("title")]
    [InlineData("created")]
    [InlineData("updated")]
    [InlineData("tags")]
    [InlineData("aliases")]
    [InlineData("source")]
    [InlineData("source_url")]
    [InlineData("occurred_at")]
    public void ReservedBaseFieldsAreRejectedInsideProps(string field)
    {
        var props = JsonSerializer.SerializeToElement(new Dictionary<string, object> { [field] = "claimed" });
        var result = _registry.ValidateProperties("article", props);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Keyword == "reserved-field" && error.PropertyPath == $"/{field}");
        var exception = Assert.Throws<PropertyValidationException>(() => _registry.DeriveEffectiveProperties("note", props));
        Assert.Contains(exception.Validation.Errors, error => error.Keyword == "reserved-field");
    }

    [Fact]
    public void ArticleAnnotationsBecomeEngineMetadata()
    {
        var metadata = _registry.Get("article").Metadata;
        Assert.Equal("published_at", metadata.OccurredBasis);
        Assert.Equal(["author", "site"], metadata.IndexedProperties.Order());
        Assert.Equal("author", metadata.PeopleRoles["author"]);
        Assert.Empty(metadata.NaturalKeyProperties);
        Assert.Equal(3, metadata.Annotations.Count);
        Assert.Equal("created", _registry.Get("note").Metadata.OccurredBasis);
        Assert.Null(_registry.Get("file").Metadata.OccurredBasis);
    }

    [Fact]
    public void ParsesNaturalKeysAndPreservesUnknownAnnotations()
    {
        var definition = new ContentTypeDefinition("custom", 1, """
            {"type":"object","properties":{"uid":{"type":"string"},"recurrence_id":{"type":"string"}},
             "additionalProperties":false,"x-natural-key":"uid+recurrence_id","x-renderer":{"kind":"card"}}
            """);
        Assert.Equal(["uid", "recurrence_id"], definition.Metadata.NaturalKeyProperties);
        Assert.Equal("card", definition.Metadata.Annotations["x-renderer"].GetProperty("kind").GetString());
    }

    [Fact]
    public void DefinitionsCannotDeclareReservedProperties()
    {
        Assert.Throws<ArgumentException>(() => new ContentTypeDefinition("custom", 1,
            """{"type":"object","properties":{"title":{"type":"string"}},"additionalProperties":false}"""));
    }

    [Fact]
    public void AnnotationReferencesMustExistInTheSchema()
    {
        Assert.Throws<ArgumentException>(() => new ContentTypeDefinition("custom", 1,
            """{"type":"object","properties":{},"additionalProperties":false,"x-index":["typo"]}"""));
    }

    [Fact]
    public void DerivationDropsUnknownFieldsWithoutChangingItsInput()
    {
        var original = Parse("""{"author":"Sarah","arbitrary":{"number":1.0,"nested":[null,true]}}""");
        var effective = _registry.DeriveEffectiveProperties("article", original);
        Assert.Single(effective.EnumerateObject());
        Assert.Equal("Sarah", effective.GetProperty("author").GetString());
        Assert.Equal("1.0", original.GetProperty("arbitrary").GetProperty("number").GetRawText());
    }

    [Fact]
    public void DerivationRejectsMalformedKnownFields()
    {
        Assert.Throws<PropertyValidationException>(() => _registry.DeriveEffectiveProperties("article", Parse("""{"author":7}""")));
    }

    [Fact]
    public void RemovedCustomTypeProcessesAsFile()
    {
        Assert.Equal("file", _registry.GetProcessingType("no_longer_configured").Key);
        Assert.False(_registry.TryGet("no_longer_configured", out var definition));
        Assert.Null(definition);
        Assert.Throws<KeyNotFoundException>(() => _registry.Get("no_longer_configured"));
    }

    internal static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
