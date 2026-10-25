using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace SecondBrain.Server.Tests.Support;

/// <summary>Controlled native protocol fixture; no cloud service or private document is used.</summary>
internal sealed class NativeCitationProviderFixture : IAsyncDisposable
{
    private WebApplication app=null!;
    public Uri Endpoint {get;private set;}=null!;
    public bool BadIndex;
    public string[] Documents=[];
    public Func<Task>? BeforeResponse {get;set;}
    public string? ReadDocumentFirst {get;set;}
    public int Calls {get;private set;}
    public string LastRequest {get;private set;}="";
    public static async Task<NativeCitationProviderFixture> CreateAsync()
    {
        var f=new NativeCitationProviderFixture();var builder=WebApplication.CreateBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");f.app=builder.Build();
        f.app.MapGet("/v1/models",()=>Results.Json(new {data=new[]{new {id="native-fixture"}},has_more=false}));
        f.app.MapPost("/v1/messages",async(HttpContext context)=> {
            using var body=await JsonDocument.ParseAsync(context.Request.Body);
            f.Calls++;f.LastRequest=body.RootElement.GetRawText();
            if(f.ReadDocumentFirst is { } readId&&f.Calls==1) {
                await context.Response.WriteAsJsonAsync(new {id="msg_read",type="message",role="assistant",model="native-fixture",content=new[]{new {type="tool_use",id="call_read",name="read_document",input=new {id=readId}}},stop_reason="tool_use",usage=new {input_tokens=20,output_tokens=4}});return;
            }
            f.Documents=body.RootElement.GetProperty("messages").EnumerateArray().SelectMany(message=>
                message.GetProperty("content").ValueKind==JsonValueKind.Array?message.GetProperty("content").EnumerateArray().ToArray():Array.Empty<JsonElement>())
                .Where(content=>content.GetProperty("type").GetString()=="document").Select(content=>content.GetProperty("source").GetProperty("data").GetString()!).ToArray();
            var index=Array.FindIndex(f.Documents,text=>text.Contains("Blue Otter",StringComparison.Ordinal));
            var quote="Blue Otter";var start=index<0?-1:f.Documents[index].IndexOf(quote,StringComparison.Ordinal);
            object? citation=index<0?null:new {type="char_location",document_index=f.BadIndex?99:index,document_title="Provider title is not authoritative",start_char_index=start,end_char_index=start+quote.Length,cited_text=quote};
            var text=index<0?"No supporting text was supplied.":"The code name is Blue Otter.";
            if(f.BeforeResponse is { } callback)await callback();
            if(!body.RootElement.TryGetProperty("stream",out var streaming)||!streaming.GetBoolean()) {
                await context.Response.WriteAsJsonAsync(new {id="msg_fixture",type="message",role="assistant",model="native-fixture",content=new[]{new {type="text",text,citations=citation is null?Array.Empty<object>():new[]{citation}}},stop_reason="end_turn",usage=new {input_tokens=30,output_tokens=8}});return;
            }
            context.Response.ContentType="text/event-stream";
            async Task Event(string type,object value)=>await context.Response.WriteAsync("event: "+type+"\ndata: "+JsonSerializer.Serialize(value)+"\n\n");
            await Event("message_start",new {type="message_start",message=new {id="msg_fixture",type="message",role="assistant",model="native-fixture",content=Array.Empty<object>(),stop_reason=(string?)null,usage=new {input_tokens=30,output_tokens=0}}});
            await Event("content_block_start",new {type="content_block_start",index=0,content_block=new {type="text",text=""}});
            await Event("content_block_delta",new {type="content_block_delta",index=0,delta=new {type="text_delta",text}});
            if(citation is not null)await Event("content_block_delta",new {type="content_block_delta",index=0,delta=new {type="citations_delta",citation}});
            await Event("content_block_stop",new {type="content_block_stop",index=0});
            await Event("message_delta",new {type="message_delta",delta=new {stop_reason="end_turn",stop_sequence=(string?)null},usage=new {output_tokens=8}});
            await Event("message_stop",new {type="message_stop"});
        });
        await f.app.StartAsync();f.Endpoint=new(f.app.Urls.Single()+"/");return f;
    }
    public async ValueTask DisposeAsync(){await app.StopAsync();await app.DisposeAsync();}
}
