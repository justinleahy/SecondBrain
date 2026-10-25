using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using SecondBrain.Core.Chat;

namespace SecondBrain.Providers.OpenAICompatible;

internal static class AnthropicCitations
{
    public static CitationSourceContent[] Prepare(List<ChatMessage> messages,bool supported)
    {
        var sources=new List<CitationSourceContent>();
        foreach(var message in messages) for(var i=0;i<message.Contents.Count;i++) {
            var content=message.Contents[i];
            if(content is not CitationSourceContent source) {
                if(supported && (content is DataContent || content.RawRepresentation is ContentBlockParam raw && raw.Value is DocumentBlockParam))
                    throw new InvalidOperationException("Native citations require captured inline source content.");
                continue;
            }
            if(!supported || message.Role!=ChatRole.User || !NativeCitationAnswer.ValidSource(source.Source) || sources.Count>=100 || sources.Any(item=>item.Source.Number==source.Source.Number))
                throw new InvalidOperationException("Invalid native citation source.");
            sources.Add(source);
            var document=DocumentBlockParam.FromRawUnchecked(new Dictionary<string,JsonElement> {
                ["type"]=JsonSerializer.SerializeToElement("document"),
                ["source"]=JsonSerializer.SerializeToElement(new {type="text",media_type="text/plain",data=source.Source.Excerpt}),
                ["title"]=JsonSerializer.SerializeToElement("Source "+source.Source.Number),
                ["citations"]=JsonSerializer.SerializeToElement(new {enabled=true})
            });
            message.Contents[i]=new TextContent("") {RawRepresentation=new ContentBlockParam(document)};
        }
        return sources.ToArray();
    }
    public static IList<AIContent> Expand(IList<AIContent> contents,CitationSourceContent[] sources)
    {
        List<AIContent> result=[];
        foreach(var content in contents) {
            result.Add(content);
            if(content is not TextContent text) continue;
            if(text.RawRepresentation is TextBlock {Citations:{ } citations})
                foreach(var citation in citations) result.Add(Candidate(JsonSerializer.SerializeToElement(citation),sources));
            else if(text.RawRepresentation is CitationsDelta delta)
                result.Add(Candidate(JsonSerializer.SerializeToElement(delta.Citation),sources));
        }
        return result;
    }
    private static CitationSpanContent Candidate(JsonElement value,CitationSourceContent[] sources)
    {
        static CitationSpanContent Invalid()=>new(-1,-1,-1,"");
        try {
            if(value.GetProperty("type").GetString()!="char_location" || !value.GetProperty("document_index").TryGetInt32(out var index) || index<0 || index>=sources.Length ||
                !value.GetProperty("start_char_index").TryGetInt32(out var start) || !value.GetProperty("end_char_index").TryGetInt32(out var end) || start<0 || end<=start) return Invalid();
            var source=sources[index].Source;var quote=value.GetProperty("cited_text").GetString();
            if(string.IsNullOrWhiteSpace(quote) || quote.Length>source.Excerpt.Length) return Invalid();
            // The contract says character ranges. Normalize only a unique exact match:
            // UTF-16 and Unicode-scalar conventions may differ around supplementary characters.
            var ranges=new HashSet<(int Start,int End)>();
            void Match(int a,int b) {
                if(a>=0&&b>a&&b<=source.Excerpt.Length && source.Excerpt[a..b]==quote) ranges.Add((a,b));
            }
            Match(start,end);
            var offset=0;var ordinal=0;var from=-1;var to=-1;
            foreach(var rune in source.Excerpt.EnumerateRunes()) {
                if(ordinal==start) from=offset;if(ordinal==end) {to=offset;break;}
                offset+=rune.Utf16SequenceLength;ordinal++;
            }
            if(ordinal==start) from=offset;if(ordinal==end) to=offset;
            Match(from,to);
            if(ranges.Count!=1) return Invalid();var range=ranges.Single();
            return new(source.Number,range.Start,range.End,quote);
        } catch(Exception error) when(error is InvalidOperationException or KeyNotFoundException or ArgumentException) {return Invalid();}
    }
}
