using Microsoft.AspNetCore.Http.Features;
using SecondBrain.Core.Problems;
using SecondBrain.Server.Http;

namespace SecondBrain.Server.Auth;

/// <summary>Marks an endpoint whose body carries the account password.</summary>
public sealed class PasswordRequest;

/// <summary>
/// Admits password requests without waiting, globally and per source, then buffers at most <see cref="MaxBodyBytes"/>
/// before antiforgery, form or JSON parsing reads the body. Runs after the Host/Origin/Access gates and before authentication.
/// </summary>
public sealed class PasswordRequestMiddleware(RequestDelegate next, TimeProvider clock)
{
    /// <summary>A fully <c>\u00XX</c>-escaped 1,024-byte password is about 6.2 KiB of JSON; a form is about 3.5 KiB.</summary>
    public const int MaxBodyBytes = 16 * 1024;
    public const int GlobalConcurrency = 8;
    public const int PerSourceConcurrency = 4;
    public static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock gate = new();
    // Entries exist only while a request is admitted, so the map never exceeds GlobalConcurrency.
    private readonly Dictionary<string, int> sources = new(StringComparer.Ordinal);
    private int active;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<PasswordRequest>() is null)
        {
            await next(context);
            return;
        }
        var source = AuthEndpoints.Source(context);
        if (!TryEnter(source))
        {
            context.Response.Headers.RetryAfter = "1";
            await ProblemResponses.WriteAsync(context, 429, ProblemTypes.LimitExceeded, "Too many concurrent password requests.");
            return;
        }
        try
        {
            if (context.Request.ContentLength > MaxBodyBytes)
            {
                await TooLargeAsync(context);
                return;
            }
            // Defence in depth on servers that enforce it; the bounded read below is authoritative.
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxBodyBytes;
            var buffer = new byte[MaxBodyBytes + 1];
            var length = 0;
            using (var timeout = new CancellationTokenSource(BodyTimeout, clock))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, timeout.Token))
            {
                try
                {
                    int read;
                    while (length < buffer.Length && (read = await context.Request.Body.ReadAsync(buffer.AsMemory(length), linked.Token)) > 0)
                        length += read;
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
                {
                    await ProblemResponses.WriteAsync(context, 408, ProblemTypes.InvalidRequest, "The request body was not received in time.");
                    return;
                }
                catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
                {
                    await TooLargeAsync(context);
                    return;
                }
            }
            if (length > MaxBodyBytes)
            {
                await TooLargeAsync(context);
                return;
            }
            var original = context.Request.Body;
            context.Request.Body = new MemoryStream(buffer, 0, length, writable: false);
            try { await next(context); }
            finally { context.Request.Body = original; }
        }
        finally { Exit(source); }
    }

    private static Task TooLargeAsync(HttpContext context) =>
        ProblemResponses.WriteAsync(context, 413, ProblemTypes.RequestTooLarge, "The password request body is too large.");

    private bool TryEnter(string source)
    {
        lock (gate)
        {
            sources.TryGetValue(source, out var count);
            if (active >= GlobalConcurrency || count >= PerSourceConcurrency) return false;
            active++;
            sources[source] = count + 1;
            return true;
        }
    }

    private void Exit(string source)
    {
        lock (gate)
        {
            active--;
            if (sources[source] == 1) sources.Remove(source);
            else sources[source]--;
        }
    }
}
