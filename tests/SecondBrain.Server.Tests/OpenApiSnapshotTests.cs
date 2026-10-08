using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

/// <summary>Pins the public HTTP contract: operations and component schemas (OpenAPI schema IDs).</summary>
public sealed class OpenApiSnapshotTests
{
    private static readonly HashSet<string> OperationKeys = new(StringComparer.Ordinal)
    {
        "get", "put", "post", "delete", "options", "head", "patch", "trace",
    };

    [Fact]
    public async Task OpenApiMatchesGolden()
    {
        await using var factory = new LaneDWebFactory();
        using var client = factory.CreatePrivateClient();
        using var response = await client.GetAsync("/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        var operations = new List<string>();
        foreach (var (path, item) in document["paths"]!.AsObject())
            foreach (var (method, _) in item!.AsObject())
                if (OperationKeys.Contains(method)) operations.Add(method.ToUpperInvariant() + " " + path);
        operations.Sort(StringComparer.Ordinal);

        var snapshot = new JsonObject
        {
            ["operations"] = new JsonArray(operations.Select(operation => (JsonNode)JsonValue.Create(operation)).ToArray()),
            ["schemas"] = Sorted(document["components"]?["schemas"]),
        };
        var json = snapshot.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        GoldenFile.AssertMatches("openapi.json", json.ReplaceLineEndings("\n") + "\n");
    }

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject value => new JsonObject(value.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, Sorted(pair.Value)))),
        JsonArray value => new JsonArray(value.Select(Sorted).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };
}
