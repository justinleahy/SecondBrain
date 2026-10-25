using System.Text.Json;
using Microsoft.JSInterop;
using SecondBrain.Core.Authorization;

namespace SecondBrain.Server.Components;

/// <summary>Calls the public API with the browser's cookie, Origin and a fresh antiforgery token.</summary>
public sealed class BrowserApi(IJSRuntime javascript)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<T> SendAsync<T>(string path, string method = "GET", object? body = null,
        long? revision = null, string? requestId = null)
    {
        var response = await javascript.InvokeAsync<BrowserResponse>("secondBrainApi.send", path, method, body, revision, requestId);
        if (response.Status is < 200 or >= 300)
            throw new BrowserApiException(response.Status, response.Body);
        if (response.Body.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return default!;
        return response.Body.Deserialize<T>(Json)!;
    }

    public async Task<BrowserPageResult<T>> PageAsync<T>(string path)
    {
        var response = await javascript.InvokeAsync<BrowserResponse>("secondBrainApi.send", path, "GET", null, null, null);
        if (response.Status is < 200 or >= 300) throw new BrowserApiException(response.Status, response.Body);
        return new(response.Body.Deserialize<T[]>(Json) ?? [], response.Next);
    }

    public async Task NavigateAsync(string path, string destination, string method = "POST", object? body = null)
    {
        var response = await javascript.InvokeAsync<BrowserResponse>("secondBrainApi.sendAndNavigate", path, method, body, destination);
        if (response.Status is < 200 or >= 300) throw new BrowserApiException(response.Status, response.Body);
    }

    private sealed record BrowserResponse(int Status, JsonElement Body, string? Next = null);
}

public sealed record BrowserPageResult<T>(IReadOnlyList<T> Items, string? Next);

// JSON arrays deserialize into a concrete collection; AuthenticatedCredential's IReadOnlySet
// is an application authority type, not a supported System.Text.Json collection contract.
public sealed record BrowserSessionIdentity(string Id, Scope[] Scopes);

public sealed class BrowserApiException(int status, JsonElement problem) : Exception(MessageFor(status, problem))
{
    public int Status { get; } = status;
    public string? ProblemType { get; } = problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("type", out var type) ? type.GetString() : null;
    public string? Detail(string name) => problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string MessageFor(int status, JsonElement problem) => status switch
    {
        401 => "Your session has expired. Sign in again.",
        403 when problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("type", out var type) && type.GetString()?.Contains("step-up", StringComparison.Ordinal) == true
            => "Confirm your password in Settings before making this change.",
        403 => "Your session does not have permission for this action.",
        404 => "This item is no longer available.",
        412 => "The document changed since you opened it. Your edits have been kept. Compare the latest revision before saving again.",
        429 => "The server is busy. Please try again shortly.",
        _ => problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("title", out var title)
            ? title.GetString() ?? "The request failed. Please try again."
            : "The request failed. Please try again."
    };
}
