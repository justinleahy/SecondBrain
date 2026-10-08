using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;

namespace SecondBrain.Infrastructure.Configuration;

/// <summary>Adapter exposes the validated Core snapshots through the standard monitor contract.</summary>
public sealed class SecondBrainOptionsMonitor(ReloadingConfiguration configuration) : IOptionsMonitor<SecondBrainOptions>
{
    public SecondBrainOptions CurrentValue => configuration.CurrentValue;
    public SecondBrainOptions Get(string? name) => CurrentValue;
    public IDisposable OnChange(Action<SecondBrainOptions, string?> listener)
    {
        void Handler(SecondBrainOptions options) => listener(options, Options.DefaultName);
        configuration.Changed += Handler;
        return new Subscription(() => configuration.Changed -= Handler);
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? action = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
}
