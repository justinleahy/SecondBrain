using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace SecondBrain.Core.Chat;

/// <summary>A captured, hash-verified passage sent as an inline citable provider document.</summary>
public sealed class CitationSourceContent(ChatSource source) : AIContent
{
    public ChatSource Source {get;}=source;
}
/// <summary>Provider-neutral candidate with UTF-16 offsets into one captured passage. Never trusted without verification.</summary>
public sealed class CitationSpanContent(int sourceNumber,int start,int end,string quote) : AIContent
{
    public int SourceNumber {get;}=sourceNumber;
    public int Start {get;}=start;
    public int End {get;}=end;
    public string Quote {get;}=quote;
}
public sealed record ChatCitationSpan(int Start,int End,string Excerpt,string Sha256);

/// <summary>Only exact captured substrings can create native evidence markers. Model-written markers do not.</summary>
public sealed partial class NativeCitationAnswer(int maxQuoteBytes=200_000)
{
    private readonly Dictionary<int,ChatSource> sources=[];
    private readonly Dictionary<int,List<ChatCitationSpan>> spans=[];
    private readonly StringBuilder text=new();
    private int invalid,seen,quoteBytes;
    public static bool ValidSource(ChatSource source)=>source.Number>0 && source.Excerpt is not null &&
        string.Equals(source.Sha256,Hash(source.Excerpt),StringComparison.OrdinalIgnoreCase);
    public void Register(IReadOnlyList<ChatSource> captured)
    {
        foreach(var source in captured) {
            if(!ValidSource(source)) throw new ChatConflictException("A captured citation passage is invalid.");
            if(sources.TryGetValue(source.Number,out var before)&&before!=source) throw new ChatConflictException("A captured citation changed.");
            sources[source.Number]=source;
        }
    }
    public string Append(AIContent content)
    {
        if(content is TextContent value) {
            // Reserve our internal marker delimiters; a model cannot manufacture a verified marker.
            var clean=value.Text.Replace("\uE000","",StringComparison.Ordinal).Replace("\uE001","",StringComparison.Ordinal);
            text.Append(clean);return clean;
        }
        if(content is not CitationSpanContent candidate) return "";
        if(++seen>128) throw new ChatLimitException("The response exceeded its citation limit.");
        if(!sources.TryGetValue(candidate.SourceNumber,out var source) || candidate.Start<0 || candidate.End<=candidate.Start ||
            candidate.End>source.Excerpt.Length || !Boundary(source.Excerpt,candidate.Start) || !Boundary(source.Excerpt,candidate.End) ||
            !string.Equals(source.Excerpt[candidate.Start..candidate.End],candidate.Quote,StringComparison.Ordinal)) {invalid++;return "";}
        var span=new ChatCitationSpan(candidate.Start,candidate.End,candidate.Quote,Hash(candidate.Quote));
        if(!spans.TryGetValue(candidate.SourceNumber,out var selected)) spans[candidate.SourceNumber]=selected=[];
        if(!selected.Contains(span)) {
            quoteBytes=checked(quoteBytes+Encoding.UTF8.GetByteCount(candidate.Quote));
            if(quoteBytes>Math.Clamp(maxQuoteBytes,1,1_000_000)) throw new ChatLimitException("The response exceeded its citation byte limit.");
            selected.Add(span);
        }
        text.Append('\uE000').Append(candidate.SourceNumber.ToString(CultureInfo.InvariantCulture)).Append('\uE001');
        return "["+candidate.SourceNumber.ToString(CultureInfo.InvariantCulture)+"]";
    }
    public ChatAnswer Verify()
    {
        var bad=invalid;
        var cleaned=ModelMarker().Replace(text.ToString(),_=> {bad++;return "";});
        cleaned=OwnedMarker().Replace(cleaned,match=>"["+match.Groups[1].Value+"]");
        List<string> warnings=[];if(bad>0) warnings.Add("invalid_citations_removed");if(spans.Count==0) warnings.Add("no_verified_sources");
        return new(cleaned,spans.OrderBy(pair=>pair.Key).Select(pair=>new ChatCitation(sources[pair.Key],Spans:pair.Value.ToArray())).ToArray(),bad,warnings.ToArray());
    }
    private static bool Boundary(string value,int offset)=>offset==0||offset==value.Length||!(char.IsHighSurrogate(value[offset-1])&&char.IsLowSurrogate(value[offset]));
    private static string Hash(string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    [GeneratedRegex(@"\[(\d+)\]",RegexOptions.CultureInvariant)] private static partial Regex ModelMarker();
    [GeneratedRegex("\uE000([0-9]+)\uE001",RegexOptions.CultureInvariant)] private static partial Regex OwnedMarker();
}
