using System.Text.Json;
using SecondBrain.Core.Chat;

namespace SecondBrain.Server.Chat;

/// <summary>Preserves source metadata while sending the captured text once as citable documents.</summary>
internal static class NativeCitationPrompt
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static object Summary(object? value,IReadOnlyList<ChatSource>? registered=null)
    {
        var element=JsonSerializer.SerializeToElement(value,Json);
        if(element.ValueKind!=JsonValueKind.Array || element.GetArrayLength()==0) return value??element;
        if(element.EnumerateArray().Any(item=>item.ValueKind!=JsonValueKind.Object || !item.TryGetProperty("excerpt",out _) || !item.TryGetProperty("sha256",out _))) return value??element;
        ChatSource[]? sources;
        try {sources=element.Deserialize<ChatSource[]>(Json);}catch(JsonException) {return value??element;}
        if(sources is null || sources.Any(source=>source is null || !NativeCitationAnswer.ValidSource(source) || registered is not null && !registered.Contains(source))) return value??element;
        return sources.Select(source=>new {source.Number,source.DocumentId,source.Revision,source.Generation,source.ChunkId,source.Ordinal,source.Page,source.Title,source.Transcript}).ToArray();
    }
}
