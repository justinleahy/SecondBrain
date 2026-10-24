using System.Globalization;
using System.Text.RegularExpressions;
using SecondBrain.Core.Chat;

namespace SecondBrain.Server.Chat;

/// <summary>Numbers only the cited sources for display without changing the verified source registry.</summary>
public static partial class ChatAnswerPresentation
{
    public static ChatAnswer ForDisplay(ChatAnswer answer)
    {
        var numbers = answer.Citations.Select((citation, index) => (citation.Source.Number, Display: index + 1))
            .ToDictionary(item => item.Number, item => item.Display);
        return answer with
        {
            Text = Marker().Replace(answer.Text, match =>
                int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var original)
                && numbers.TryGetValue(original, out var display)
                    ? "[" + display.ToString(CultureInfo.InvariantCulture) + "]" : match.Value),
            Citations = answer.Citations.Select(citation => citation with
            {
                Source = citation.Source with { Number = numbers[citation.Source.Number] }
            }).ToArray()
        };
    }

    [GeneratedRegex(@"\[(\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex Marker();
}
