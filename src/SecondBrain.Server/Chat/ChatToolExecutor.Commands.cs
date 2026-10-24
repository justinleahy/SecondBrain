using System.Text.Json;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Retrieval;

namespace SecondBrain.Server.Chat;

internal sealed partial class ChatToolExecutor
{
    public async Task<ChatAnswer> SaveCommandAsync(string content, CancellationToken token)
    {
        var title = content.Split('\n', 2)[0].Trim();
        title = title[..Math.Min(title.Length, 100)];
        var result = JsonSerializer.SerializeToElement(await CreateAsync(content, title, cancellationToken: token));
        var status = result.GetProperty("Status").GetString();
        var id = result.GetProperty("Id").GetString();
        return new(status == "executed" ? $"Saved note. Proposal: {id}." : $"Note proposed for review. Proposal: {id}. Approve it to save the note.", []);
    }

    public async Task<ChatAnswer> QueryCommandAsync(QueryRequest query, CancellationToken token) =>
        MetadataAnswer(await QueryAsync(query, token));

    public async Task<ChatAnswer> RecentCommandAsync(int count, CancellationToken token) =>
        MetadataAnswer(await RecentAsync(count, token));

    private ChatAnswer MetadataAnswer(object value)
    {
        var page = JsonSerializer.SerializeToElement(value);
        var records = page.GetProperty("records").EnumerateArray().ToArray();
        var text = records.Length == 0 ? "No matching records were found." : string.Join("\n", records.Select(record =>
            $"{record.GetProperty("Title").GetString()} · {record.GetProperty("Type").GetString()} · {record.GetProperty("Id").GetString()} · revision {record.GetProperty("Revision").GetInt64()}"));
        if (page.TryGetProperty("NextCursor", out var cursor) && cursor.ValueKind == JsonValueKind.String)
            text += "\nMore records are available. Query cursor: " + cursor.GetString();
        if (page.TryGetProperty("Coverage", out var coverage) && coverage.ValueKind is not JsonValueKind.Null)
            text += "\nCoverage: " + coverage.GetRawText();
        return new(text, [], Warnings: Warnings.ToArray());
    }
}
