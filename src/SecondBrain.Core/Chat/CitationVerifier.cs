using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SecondBrain.Core.Chat;

/// <summary>Only registered, hash-verified excerpts can become source links. Model-produced IDs are never trusted.</summary>
public static partial class CitationVerifier
{
    public static ChatAnswer VerifyAnswer(ChatAnswer answer,IReadOnlyList<ChatSource> sources,int maxSpanBytes=1_000_000)
    {
        var verified=Verify(answer.Text,sources);
        var rejected=new HashSet<int>();var selected=new List<ChatCitation>();var spanBytes=0;
        foreach(var citation in verified.Citations) {
            var candidates=answer.Citations.Where(value=>value.Source.Number==citation.Source.Number&&value.Spans is {Count:>0}).ToArray();
            if(candidates.Length==0) {selected.Add(citation);continue;}
            var candidate=candidates[0];var valid=candidates.Length==1&&candidate.Source==citation.Source&&candidate.Spans!.Count<=128;
            if(valid) foreach(var span in candidate.Spans!) {
                if(span is null||span.Start<0||span.End<=span.Start||span.End>citation.Source.Excerpt.Length ||
                    !Boundary(citation.Source.Excerpt,span.Start)||!Boundary(citation.Source.Excerpt,span.End)||
                    !string.Equals(citation.Source.Excerpt[span.Start..span.End],span.Excerpt,StringComparison.Ordinal)||
                    !string.Equals(span.Sha256,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(span.Excerpt))),StringComparison.OrdinalIgnoreCase)) {valid=false;break;}
                spanBytes=checked(spanBytes+Encoding.UTF8.GetByteCount(span.Excerpt));
                if(spanBytes>Math.Clamp(maxSpanBytes,1,1_000_000)) {valid=false;break;}
            }
            if(valid) selected.Add(citation with {Spans=candidate.Spans!.ToArray()});else rejected.Add(citation.Source.Number);
        }
        var invalid=verified.InvalidCitationCount;
        var text=Marker().Replace(verified.Text,match=>int.TryParse(match.Groups[1].Value,out var number)&&rejected.Contains(number)?Remove():match.Value);
        string Remove() {invalid++;return "";}
        var warnings=(verified.Warnings??[]).Where(warning=>warning!="no_verified_sources").ToList();
        if(rejected.Count>0) warnings.Add("invalid_citations_removed");if(selected.Count==0) warnings.Add("no_verified_sources");
        return new(text,selected,invalid,warnings.Distinct().ToArray());
    }
    private static bool Boundary(string value,int offset)=>offset==0||offset==value.Length||!(char.IsHighSurrogate(value[offset-1])&&char.IsLowSurrogate(value[offset]));
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
