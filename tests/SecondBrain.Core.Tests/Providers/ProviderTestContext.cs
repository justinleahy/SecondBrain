using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;
using SecondBrain.Infrastructure.Network;
using SecondBrain.MockProvider;
using SecondBrain.Providers.OpenAICompatible;

namespace SecondBrain.Core.Tests.Providers;

internal sealed class ProviderTestContext : IAsyncDisposable
{
    private readonly WebApplication app;
    public Uri Endpoint { get; }
    public MockProviderState Mock { get; }
    public TestOptionsMonitor Options { get; }
    public PrivacyPolicy Privacy { get; }
    public PolicyHttpClientFactory Factory { get; }
    public ModelCatalog Catalog { get; } = new();
    public ProviderRegistry Registry { get; }

    private ProviderTestContext(WebApplication app, IDnsResolver? resolver)
    {
        this.app = app;
        Endpoint = new Uri(app.Urls.Single().TrimEnd('/') + "/v1");
        Mock = app.Services.GetRequiredService<MockProviderState>();
        Options = new(CreateOptions(Endpoint));
        resolver ??= new SystemDnsResolver();
        Privacy = new(Options, resolver);
        Factory = new(Privacy, resolver);
        Registry = new(Options, Factory, Privacy, Catalog, new EnvironmentProviderCredentialResolver());
    }

    public static async Task<ProviderTestContext> StartAsync(IDnsResolver? resolver = null)
    {
        var app = MockProviderApplication.Build([], builder => builder.WebHost.UseUrls("http://127.0.0.1:0"));
        await app.StartAsync();
        return new(app, resolver);
    }

    public static SecondBrainOptions CreateOptions(Uri endpoint) => new()
    {
        Providers = new(StringComparer.Ordinal)
        {
            ["test-local"] = new() { Kind = "openai_compatible", Endpoint = endpoint.AbsoluteUri },
        },
        Models = new()
        {
            Chat = new() { Provider = "test-local", Model = "mock-chat" },
            Enrich = new() { Provider = "test-local", Model = "mock-chat" },
            Embed = new() { Provider = "test-local", Model = "mock-embed" },
        },
        Privacy = new() { LocalOnly = true, EgressCanary = false, TrustedServices = [endpoint.GetLeftPart(UriPartial.Authority)] },
    };

    public async ValueTask DisposeAsync()
    {
        Registry.Dispose();
        Factory.Dispose();
        Privacy.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

internal sealed class TestOptionsMonitor(SecondBrainOptions current) : IOptionsMonitor<SecondBrainOptions>
{
    private readonly List<Action<SecondBrainOptions, string?>> listeners = [];
    public SecondBrainOptions CurrentValue { get; private set; } = current;
    public SecondBrainOptions Get(string? name) => CurrentValue;
    public IDisposable OnChange(Action<SecondBrainOptions, string?> listener)
    {
        listeners.Add(listener);
        return new Subscription(() => listeners.Remove(listener));
    }
    public void Reload(SecondBrainOptions value)
    {
        var old = CurrentValue;
        CurrentValue = value;
        try { foreach (var listener in listeners.ToArray()) listener(value, null); }
        catch { CurrentValue = old; throw; }
    }
    private sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
