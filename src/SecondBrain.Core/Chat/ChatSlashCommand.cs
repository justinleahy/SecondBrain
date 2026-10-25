using System.Globalization;
using System.Text.Json;
using SecondBrain.Core.Retrieval;

namespace SecondBrain.Core.Chat;

/// <summary>Shared syntax for explicit assistant actions, independent of a model or client.</summary>
public sealed record ChatSlashCommand(string Name, string Argument, QueryRequest? Query = null)
{
    public static ChatSlashCommand? Parse(string prompt)
    {
        var text = prompt.Trim();
        if (!text.StartsWith('/')) return null;
        var split = text.IndexOfAny([' ', '\t', '\r', '\n']);
        var name = split < 0 ? text : text[..split];
        var argument = split < 0 ? "" : text[(split + 1)..].Trim();
        switch (name)
        {
            case "/save": return new(name, argument);
            case "/search" when argument.Length is > 0 and <= 2048: return new(name, argument);
            case "/recent":
                if (argument.Length == 0) return new(name, "10");
                if (int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is >= 1 and <= 50)
                    return new(name, argument);
                throw new ArgumentException("Use /recent or /recent 1–50.");
            case "/query":
                var separator = argument.IndexOfAny([' ', '\t', '\r', '\n']);
                var type = separator < 0 ? argument : argument[..separator];
                if (type.Length == 0) throw new ArgumentException("Use /query <type> followed optionally by a JSON filter object.");
                QueryRequest query = new(Type: type, Limit: 10);
                if (separator >= 0)
                {
                    using var json = JsonDocument.Parse(argument[(separator + 1)..]);
                    string[] allowed = ["tags", "people", "predicates", "occurred", "updated", "sort", "direction", "limit", "cursor", "source", "path_prefix", "mime", "include_cancelled"];
                    if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)))
                        throw new ArgumentException("Unknown query filter. Use tags, people, predicates, occurred, updated, sort, direction, limit, cursor, source, path_prefix or mime.");
                    query = json.RootElement.Deserialize<QueryRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))! with { Type = type };
                }
                if (query.Limit is < 1 or > 50) throw new ArgumentException("Query limit must be 1 through 50.");
                return new(name, argument, query);
            case "/new" or "/sources" or "/pending" when argument.Length == 0: return new(name, "");
            default: throw new ArgumentException("Use /save [text], /new, /search <query>, /query <type> [JSON filters], /recent [count], /sources or /pending.");
        }
    }
}
