using System.Text.Json;
using Microsoft.JSInterop;
using SecondBrain.Core.Authorization;
using SecondBrain.Server.Components;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class BrowserApiTests
{
    [Fact]
    public async Task SessionIdentityDeserializesTheActualNumericScopeArrayContract()
    {
        var api = new BrowserApi(new JsonRuntime("""{"status":200,"body":{"id":"session-id","kind":"session","generation":1,"accountEpoch":1,"scopes":[0,1,2,3],"steppedUpAt":null,"isCliSession":false}}"""));
        var identity = await api.SendAsync<BrowserSessionIdentity>("/auth/me");
        Assert.Equal("session-id", identity.Id);
        Assert.Equal(Enum.GetValues<Scope>(), identity.Scopes);
    }

    [Fact]
    public async Task CredentialPaginationPreservesTheServersContinuation()
    {
        var api = new BrowserApi(new JsonRuntime("""{"status":200,"body":[{"id":"a","scopes":[0]}],"next":"/auth/sessions?after=opaque"}"""));
        var page = await api.PageAsync<BrowserSessionIdentity>("/auth/sessions");
        Assert.Equal("a", Assert.Single(page.Items).Id);
        Assert.Equal("/auth/sessions?after=opaque", page.Next);
    }

    [Fact]
    public async Task RevisionConflictsRemainTypedErrorsForDraftRecovery()
    {
        var api = new BrowserApi(new JsonRuntime("""{"status":412,"body":{"type":"https://secondbrain.dev/problems/revision-mismatch","title":"revision-mismatch","current_revision":2}}"""));
        var error = await Assert.ThrowsAsync<BrowserApiException>(() => api.SendAsync<JsonElement>("/documents/a", "PATCH", new { content = "draft" }, 1));
        Assert.Equal(412, error.Status);
        Assert.Contains("Your edits have been kept", error.Message);
    }

    private sealed class JsonRuntime(string json) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(JsonSerializer.Deserialize<TValue>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
