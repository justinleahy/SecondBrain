using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SecondBrain.Server.Components;

/// <summary>Browser API calls start after interactivity, never while prerendering.</summary>
public abstract class BrowserPage : ComponentBase
{
    [Inject] protected BrowserApi Api { get; set; } = default!;
    protected bool Busy { get; private set; }
    protected bool Loaded { get; private set; }
    protected string? Error { get; set; }
    protected virtual Task LoadAsync() => Task.CompletedTask;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        await RunAsync(LoadAsync);
        Loaded = true;
        StateHasChanged();
    }

    protected async Task RunAsync(Func<Task> action)
    {
        if (Busy) return;
        Busy = true;
        Error = null;
        try { await action(); }
        catch (BrowserApiException exception) { Error = exception.Message; }
        catch (JSDisconnectedException) { Error = "Connection lost. Reload this page to reconnect."; }
        catch (JSException) { Error = "The server could not be reached. Check your connection and try again."; }
        finally { Busy = false; }
    }
}
