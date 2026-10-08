using Microsoft.Extensions.Options;

namespace SecondBrain.Server.Tests.Support;

/// <summary>Allows configuration reload tests to update a live options monitor explicitly.</summary>
public sealed class MutableOptionsMonitor<TOptions>(TOptions initialValue) : IOptionsMonitor<TOptions>, IOptions<TOptions>
    where TOptions : class
{
    private readonly object _gate = new();
    private TOptions _value = initialValue;
    private event Action<TOptions, string?>? Changed;

    public TOptions CurrentValue => Volatile.Read(ref _value);
    public TOptions Value => CurrentValue;
    public TOptions Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<TOptions, string?> listener)
    {
        lock (_gate)
        {
            Changed += listener;
        }

        return new Subscription(this, listener);
    }

    public void Update(TOptions value)
    {
        Volatile.Write(ref _value, value);
        NotifyChanged();
    }

    public void NotifyChanged()
    {
        Action<TOptions, string?>? listeners;
        lock (_gate)
        {
            listeners = Changed;
        }

        listeners?.Invoke(CurrentValue, Options.DefaultName);
    }

    private sealed class Subscription(MutableOptionsMonitor<TOptions> owner, Action<TOptions, string?> listener) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (owner._gate)
                {
                    owner.Changed -= listener;
                }
            }
        }
    }
}
