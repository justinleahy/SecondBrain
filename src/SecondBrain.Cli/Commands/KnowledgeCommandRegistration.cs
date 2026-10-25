using System.CommandLine;
using System.Text.Json;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;

namespace SecondBrain.Cli.Commands;

internal static class KnowledgeCommandRegistration
{
    public static void Add(RootCommand root, CliServices services, Action<Command, Func<ParseResult, CancellationToken, Task<CliResult>>> bind)
    {
        IKnowledgeCommands Adapter() => services.Knowledge ?? throw new CliPreconditionException("The knowledge HTTP adapter is unavailable.");
        var enrich = new Command("enrich", "Request a model summary and suggestions for a document.");
        var enrichId = new Argument<string>("id"); enrich.Arguments.Add(enrichId);
        bind(enrich, (parse, token) => Adapter().EnrichAsync(parse.GetValue(enrichId)!, token)); root.Subcommands.Add(enrich);
        var add = new Command("add", "Capture standard input or upload a file to the daemon.");
        var path = new Argument<string>("path");
        var title = new Option<string?>("--title");
        var type = new Option<string?>("--type");
        var props = new Option<string?>("--props") { Description = "Properties as a JSON object." };
        var tags = new Option<string?>("--tags") { Description = "Comma-separated tags." };
        var wait = new Option<int>("--wait") { Description = "Wait up to 30 seconds for indexing." };
        var idempotency = new Option<string?>("--idempotency-key");
        add.Arguments.Add(path);
        foreach (var option in new Option[] { title, type, props, tags, wait, idempotency }) add.Options.Add(option);
        bind(add, (parse, token) => Adapter().AddAsync(parse.GetValue(path)!,
            new CaptureRequest("", parse.GetValue(title), ValidType(parse.GetValue(type)), Properties(parse.GetValue(props)), Tags(parse.GetValue(tags))),
            parse.GetValue(wait), parse.GetValue(idempotency), services.Input, token));
        root.Subcommands.Add(add);

        var search = new Command("search", "Find passages with keyword, semantic or hybrid retrieval.");
        var queryText = new Argument<string>("query");
        var mode = new Option<string>("--mode") { DefaultValueFactory = _ => "hybrid" };
        var searchType = new Option<string?>("--type");
        var tag = new Option<string?>("--tag");
        var person = new Option<string?>("--person");
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 8 };
        var rerank = new Option<string>("--rerank") { DefaultValueFactory = _ => "none" };
        search.Arguments.Add(queryText);
        foreach (var option in new Option[] { mode, searchType, tag, person, limit, rerank }) search.Options.Add(option);
        bind(search, (parse, token) =>
        {
            var text = parse.GetValue(queryText)!;
            var selectedMode = parse.GetValue(mode)!;
            var selectedRerank = parse.GetValue(rerank)!;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) throw new CliUsageException("Search query must contain 1–4096 characters.");
            if (selectedMode is not ("keyword" or "semantic" or "hybrid")) throw new CliUsageException("--mode must be keyword, semantic or hybrid.");
            if (selectedRerank is not ("none" or "provider" or "llm")) throw new CliUsageException("--rerank must be none, provider or llm.");
            return Adapter().SearchAsync(new SearchRequest(text, selectedMode,
                new RetrievalFilters(Type: ValidType(parse.GetValue(searchType)), Tags: Tags(parse.GetValue(tag)), People: People(parse.GetValue(person))),
                BoundedLimit(parse.GetValue(limit)), selectedRerank), token);
        });
        root.Subcommands.Add(search);

        var query = new Command("query", "Query indexed document fields without provider calls.");
        var queryType = new Option<string?>("--type") { Required = true };
        var from = new Option<string?>("--from");
        var to = new Option<string?>("--to");
        var queryPerson = new Option<string?>("--person");
        var where = new Option<string[]>("--where") { Description = "Indexed field=value equality predicate; repeat for additional fields." };
        var sort = new Option<string>("--sort") { DefaultValueFactory = _ => "updated_at" };
        var direction = new Option<string>("--direction") { DefaultValueFactory = _ => "desc" };
        var queryLimit = new Option<int>("--limit") { DefaultValueFactory = _ => 50 };
        var cursor = new Option<string?>("--cursor");
        var cancelled = new Option<bool>("--include-cancelled");
        foreach (var option in new Option[] { queryType, from, to, queryPerson, where, sort, direction, queryLimit, cursor, cancelled }) query.Options.Add(option);
        bind(query, (parse, token) =>
        {
            var selectedSort = parse.GetValue(sort)!;
            var selectedDirection = parse.GetValue(direction)!;
            if (selectedSort is not ("updated_at" or "occurred_at" or "title")) throw new CliUsageException("Unsupported --sort field.");
            if (selectedDirection is not ("asc" or "desc")) throw new CliUsageException("--direction must be asc or desc.");
            var start = Timestamp(parse.GetValue(from));
            var end = Timestamp(parse.GetValue(to));
            if (start is not null && end is not null && DateTimeOffset.Parse(start) >= DateTimeOffset.Parse(end)) throw new CliUsageException("--from must precede --to.");
            var predicates = (parse.GetValue(where) ?? []).Select(Predicate).ToArray();
            return Adapter().QueryAsync(new QueryRequest(Type: ValidType(parse.GetValue(queryType)), Predicates: predicates,
                People: People(parse.GetValue(queryPerson)), Occurred: start is null && end is null ? null : new TimeWindow(start, end),
                Sort: selectedSort, Direction: selectedDirection, Limit: BoundedLimit(parse.GetValue(queryLimit)), Cursor: parse.GetValue(cursor), IncludeCancelled: parse.GetValue(cancelled)), token);
        });
        root.Subcommands.Add(query);
    }

    private static string? ValidType(string? type) => type is null or "note" or "article" or "transcript" or "calendar_event" or "file" ? type : throw new CliUsageException("Supported types are note, article, transcript, calendar_event and file.");
    private static string[]? Tags(string? tags) => tags?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static PersonFilter[]? People(string? value) => value is null ? null : [new PersonFilter(value)];
    private static int BoundedLimit(int value) => value is >= 1 and <= 200 ? value : throw new CliUsageException("--limit must be between 1 and 200.");
    private static JsonElement? Properties(string? text)
    {
        if (text is null) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new CliUsageException("--props must be a JSON object.");
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw new CliUsageException("--props must be a valid JSON object."); }
    }
    private static PropertyPredicate Predicate(string expression)
    {
        var parts = expression.Split('=', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0])) throw new CliUsageException("--where requires field=value.");
        JsonElement value;
        try { using var document = JsonDocument.Parse(parts[1]); value = document.RootElement.Clone(); }
        catch (JsonException) { value = JsonSerializer.SerializeToElement(parts[1]); }
        return new PropertyPredicate(parts[0], "eq", value);
    }
    private static string? Timestamp(string? value)
    {
        if (value is null) return null;
        if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
            throw new CliUsageException("Time bounds must be ISO 8601 timestamps.");
        return parsed.ToString("O");
    }
}
