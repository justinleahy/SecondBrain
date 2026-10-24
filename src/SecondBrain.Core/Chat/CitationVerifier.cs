using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SecondBrain.Core.Chat;

/// <summary>Only registered, hash-verified excerpts can become source links. Model-produced IDs are never trusted.</summary>
public static partial class CitationVerifier
{
    public static ChatAnswer Verify(string text, IReadOnlyList<ChatSource> sources)
    {
        var available = sources.Where(source => string.Equals(source.Sha256,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Excerpt))), StringComparison.OrdinalIgnoreCase))
            .ToDictionary(source => source.Number);
        var cited = new HashSet<int>();
        var invalid = 0;
        var cleaned = Marker().Replace(text, match =>
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && available.ContainsKey(number))
            {
                cited.Add(number);
                return match.Value;
            }
            invalid++;
            return "";
        });
        List<string> warnings = [];
        if (invalid != 0) warnings.Add("invalid_citations_removed");
        if (cited.Count == 0) warnings.Add("no_verified_sources");
        return new ChatAnswer(cleaned, cited.Order().Select(number => new ChatCitation(available[number])).ToArray(), invalid, warnings.ToArray());
    }

    [GeneratedRegex(@"\[(\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex Marker();
}
