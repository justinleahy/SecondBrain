using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace SecondBrain.MockProvider;

/// <summary>Faults shared by discovery, chat and embeddings; no request is sent to a redirect target.</summary>
public enum MockFault
{
    None,
    Redirect,
    Timeout,
    RateLimit,
    Malformed,
}

/// <summary>Thread-safe fault selection and request counters for in-process and standalone tests.</summary>
public sealed class MockProviderState
{
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
    private int _fault;
    private long _totalRequests;
    private long _timeoutTicks = TimeSpan.FromSeconds(30).Ticks;
    private Uri _redirectTarget = new("https://example.com/mock-provider-redirect");
    private Func<HttpContext, Task>? _modelsResponse;

    public MockFault Fault
    {
        get => (MockFault)Volatile.Read(ref _fault);
        set => Volatile.Write(ref _fault, (int)value);
    }

    public Uri RedirectTarget
    {
        get => Volatile.Read(ref _redirectTarget);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!value.IsAbsoluteUri || (value.Scheme != Uri.UriSchemeHttp && value.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("The redirect target must be an absolute HTTP(S) URI.", nameof(value));
            }

            Volatile.Write(ref _redirectTarget, value);
        }
    }

    public TimeSpan Timeout
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _timeoutTicks));
        set
        {
            if (value <= TimeSpan.Zero || value > TimeSpan.FromHours(1))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Timeout must be between zero and one hour.");
            }

            Interlocked.Exchange(ref _timeoutTicks, value.Ticks);
        }
    }

    /// <summary>
    /// In-process tests may replace the /v1/models response, for example with oversized,
    /// chunked or slow discovery bodies. Null serves the standard model list.
    /// </summary>
    public Func<HttpContext, Task>? ModelsResponse
    {
        get => Volatile.Read(ref _modelsResponse);
        set => Volatile.Write(ref _modelsResponse, value);
    }

    /// <summary>Counts provider requests only; admin and liveness requests are excluded.</summary>
    public long TotalRequests => Interlocked.Read(ref _totalRequests);

    public long GetRequestCount(string path) => _counts.GetValueOrDefault(path);

    public IReadOnlyDictionary<string, long> GetRequestCounts() => new Dictionary<string, long>(_counts);

    public void ResetCounts()
    {
        _counts.Clear();
        Interlocked.Exchange(ref _totalRequests, 0);
    }

    /// <summary>Resets faults, their parameters, and counters between test scenarios.</summary>
    public void Reset()
    {
        Fault = MockFault.None;
        RedirectTarget = new Uri("https://example.com/mock-provider-redirect");
        Timeout = TimeSpan.FromSeconds(30);
        ModelsResponse = null;
        ResetCounts();
    }

    internal void Count(string path)
    {
        _counts.AddOrUpdate(path, 1, static (_, count) => count + 1);
        Interlocked.Increment(ref _totalRequests);
    }

    internal static bool TryParseFault(string? value, out MockFault fault)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "none":
                fault = MockFault.None;
                return true;
            case "redirect":
                fault = MockFault.Redirect;
                return true;
            case "timeout":
                fault = MockFault.Timeout;
                return true;
            case "429":
            case "rate-limit":
            case "rate_limit":
            case "ratelimit":
                fault = MockFault.RateLimit;
                return true;
            case "malformed":
                fault = MockFault.Malformed;
                return true;
            default:
                fault = MockFault.None;
                return false;
        }
    }
}

/// <summary>The JSON body accepted by POST /admin/fault.</summary>
public sealed record MockFaultConfiguration
{
    [JsonPropertyName("fault")]
    public string Fault { get; init; } = "none";

    [JsonPropertyName("redirect_target")]
    public string? RedirectTarget { get; init; }

    [JsonPropertyName("timeout_ms")]
    public int? TimeoutMilliseconds { get; init; }
}
