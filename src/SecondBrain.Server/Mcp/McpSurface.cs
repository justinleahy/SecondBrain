using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;

namespace SecondBrain.Server.Mcp;

public static class McpSurface
{
    public static IServiceCollection AddSecondBrainMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton<McpOperations>();
        services.AddMcpServer(options => options.ServerInfo = new() { Name = "SecondBrain", Version = "0.6" })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler(async (context, token) =>
            {
                await Operations(context.Services).AuthorizeAsync([], token);
                return new ListToolsResult { Tools = McpToolCatalog.Tools.ToList() };
            })
            .WithCallToolHandler((context, token) => Operations(context.Services).CallAsync(context.Params!.Name,
                JsonArguments(context.Params.Arguments), token))
            .WithListResourcesHandler((context, token) => Operations(context.Services).ListResourcesAsync(context.Params?.Cursor, token))
            .WithReadResourceHandler((context, token) => Operations(context.Services).ReadResourceAsync(context.Params!.Uri, token))
            .WithListResourceTemplatesHandler(async (context, token) =>
            {
                await Operations(context.Services).AuthorizeAsync([], token);
                return new ListResourceTemplatesResult { ResourceTemplates =
                [
                    new() { Name = "Document", UriTemplate = "brain://document/{id}", MimeType = "text/markdown" },
                    new() { Name = "Note", UriTemplate = "brain://note/{+path}", MimeType = "text/markdown" }
                ] };
            })
            .WithListPromptsHandler(async (context, token) =>
            {
                await Operations(context.Services).AuthorizeAsync([], token);
                return new ListPromptsResult { Prompts =
                [
                    new() { Name = "brain_daily_review", Description = "Review recent knowledge with citations." },
                    new() { Name = "brain_topic_brief", Description = "Prepare a cited brief for a topic.", Arguments = [new() { Name = "topic", Required = true }] }
                ] };
            })
            .WithGetPromptHandler((context, token) => Operations(context.Services).GetPromptAsync(context.Params!.Name, context.Params.Arguments, token))
            .WithSubscriptionsListenHandler(async (context, token) =>
            {
                await Operations(context.Services).AuthorizeAsync([Scope.Read], token);
                throw new McpProtocolException("Resource subscriptions are not advertised by this server.", McpErrorCode.MethodNotFound);
            });
        return services;
    }
    public static void MapSecondBrainMcp(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMcp("/mcp").WithMetadata(new EndpointPolicy("mcp"));
    }
    private static McpOperations Operations(IServiceProvider? services) => services!.GetRequiredService<McpOperations>();
    private static System.Text.Json.JsonElement JsonArguments(IDictionary<string, System.Text.Json.JsonElement>? arguments) =>
        System.Text.Json.JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, System.Text.Json.JsonElement>());
}

/// <summary>Transport bounds and credential kind apply before either modern discovery or compatibility dispatch.</summary>
public sealed class McpRequestMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<EndpointPolicy>()?.Operation != "mcp") { await next(context); return; }
        if (context.Items[typeof(AuthenticatedCredential)] is not AuthenticatedCredential { Kind: "api_key" })
        { await Results.Problem(statusCode: 403, title: "MCP requires an API key.").ExecuteAsync(context); return; }
        if (!HttpMethods.IsPost(context.Request.Method))
        { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        context.Response.Headers.CacheControl = "no-store";
        const int maximum = 2_000_000;
        if (context.Request.ContentLength > maximum) { context.Response.StatusCode = 413; return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        try
        {
            while ((read = await context.Request.Body.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (bounded.Length + read > maximum) { context.Response.StatusCode = 413; return; }
                await bounded.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { context.Response.StatusCode = 408; return; }
        bounded.Position = 0;
        var original = context.Request.Body;
        context.Request.Body = bounded;
        try { await next(context); }
        finally { context.Request.Body = original; }
    }
}
