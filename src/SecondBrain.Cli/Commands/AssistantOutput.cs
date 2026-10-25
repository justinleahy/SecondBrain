using System.Text;
using SecondBrain.Core.Chat;

namespace SecondBrain.Cli.Commands;

internal static class AssistantOutput
{
    // A model or imported note must not be able to execute terminal escape sequences.
    internal static string Text(string value) => string.Concat(value.EnumerateRunes()
        .Where(rune => rune.Value is '\n' or '\t' || !Rune.IsControl(rune) && Rune.GetUnicodeCategory(rune) != System.Globalization.UnicodeCategory.Format)
        .Select(rune => rune.ToString()));

    internal static async Task WriteTurnAsync(ChatTurn turn, TextWriter output, TextWriter error, string? streamed = null)
    {
        var text = turn.Redacted ? "[purged]" : turn.Answer?.Text ?? "";
        if (streamed is null || streamed != text)
        {
            if (streamed is { Length: > 0 }) await output.WriteLineAsync("Verified answer:");
            if (text.Length != 0) await output.WriteLineAsync(Text(text));
        }
        foreach (var citation in turn.Answer?.Citations ?? [])
        {
            var source = citation.Source;
            await output.WriteLineAsync(Text($"[{source.Number}] {source.Title} — {source.DocumentId}, revision {source.Revision} ({citation.State})"));
            if(citation.Spans is {Count:>0} spans)
                foreach(var span in spans) await output.WriteLineAsync(Text(span.Excerpt));
            else await output.WriteLineAsync(Text(source.Excerpt));
        }
        foreach (var warning in turn.Answer?.Warnings ?? []) await error.WriteLineAsync(Text("Warning: " + warning));
        if (turn.Status != "complete") await error.WriteLineAsync(Text($"Turn {turn.Status}: {turn.FailureCode ?? "incomplete"}."));
    }
}
