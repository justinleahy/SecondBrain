using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Sources;

namespace SecondBrain.Infrastructure.Network;

/// <summary>Single-request, descriptor-staged calendar transport; it never follows a resource in a feed.</summary>
public sealed class CalendarDownloadTransport : ICalendarDownloadTransport
{
    public const long MaximumBytes = 50L * 1024 * 1024;
    private readonly IOptionsMonitor<SecondBrainOptions> options;
    private readonly IDnsResolver dns;
    private readonly ICalendarFetchAuthority authority;
    private readonly IInputStager stager;
    private readonly Func<HttpMessageHandler>? testHandler;

    public CalendarDownloadTransport(IOptionsMonitor<SecondBrainOptions> options, IDnsResolver dns, ICalendarFetchAuthority authority, IInputStager stager)
        : this(options, dns, authority, stager, null) { }
    internal CalendarDownloadTransport(IOptionsMonitor<SecondBrainOptions> options, IDnsResolver dns, ICalendarFetchAuthority authority, IInputStager stager, Func<HttpMessageHandler>? testHandler)
    { this.options = options; this.dns = dns; this.authority = authority; this.stager = stager; this.testHandler = testHandler; }

    public async ValueTask<CalendarDownloadResult> DownloadAsync(CalendarDownloadRequest request, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        try
        {
            var link = CalendarLink.Parse(request.Url);
            var permission = PermissionFingerprint();
            await VerifyAsync(request, link, permission, token);
            await ResolveAsync(link, token);
            await VerifyAsync(request, link, permission, token);
            using var handler = testHandler?.Invoke() ?? new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.Zero, ConnectTimeout = TimeSpan.FromSeconds(15),
                MaxResponseHeadersLength = 16,
                ConnectCallback = async (context, ct) =>
                {
                    if (context.DnsEndPoint.Port != link.Uri.Port || !string.Equals(context.DnsEndPoint.Host, link.Uri.IdnHost, StringComparison.OrdinalIgnoreCase))
                        throw new CalendarSubscriptionException("calendar_origin_changed");
                    var addresses = await ResolveAsync(link, ct);
                    await VerifyAsync(request, link, permission, ct);
                    foreach (var address in addresses)
                    {
                        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                        try
                        {
                            await socket.ConnectAsync(new IPEndPoint(address, link.Uri.Port), ct);
                            await VerifyAsync(request, link, permission, ct);
                            return new CheckedConnection(new NetworkStream(socket, ownsSocket: true), t => VerifyAsync(request, link, permission, t));
                        }
                        catch (SocketException) { socket.Dispose(); }
                        catch { socket.Dispose(); throw; }
                    }
                    throw new CalendarSubscriptionException("calendar_connect_failed");
                }
            };
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            using var message = new HttpRequestMessage(HttpMethod.Get, link.Uri)
            { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            message.Headers.Accept.Add(new("text/calendar"));
            message.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
            if (request.ETag is { } etag)
            {
                if (etag.Length > 512 || !EntityTagHeaderValue.TryParse(etag, out var parsed)) throw new CalendarSubscriptionException("calendar_validator_invalid");
                message.Headers.IfNoneMatch.Add(parsed);
            }
            message.Headers.IfModifiedSince = request.Modified;
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            await VerifyAsync(request, link, permission, token);
            var status = (int)response.StatusCode;
            if (status == 304) return new("not_modified");
            if (status is >= 300 and < 400) return new("failed", Error: "calendar_redirect_refused");
            if (status != 200) return new("failed", Error: status switch
            {
                204 => "calendar_empty_response", 401 or 403 or 404 or 410 => "calendar_unavailable",
                429 => "calendar_rate_limited", >= 500 => "calendar_server_error", _ => "calendar_http_error"
            }, RetryAfterSeconds: RetryAfter(response));
            if (response.Content.Headers.ContentLength is > MaximumBytes) return new("failed", Error: "calendar_encoded_bytes");
            if (response.Content.Headers.ContentEncoding.Count > 1) return new("failed", Error: "calendar_encoding");
            var encoding = response.Content.Headers.ContentEncoding.SingleOrDefault()?.ToLowerInvariant();
            if (encoding is not (null or "identity" or "gzip" or "deflate" or "br")) return new("failed", Error: "calendar_encoding");
            var integrity = new CalendarResponseIntegrity(encoding);
            await using var raw = await response.Content.ReadAsStreamAsync(token);
            await using var encoded = new LimitedRead(raw, MaximumBytes, "calendar_encoded_bytes", (_, bytes) => { integrity.Encoded(bytes); return ValueTask.CompletedTask; });
            await using Stream decoded = encoding switch
            {
                "gzip" => new GZipStream(encoded, CompressionMode.Decompress, leaveOpen: true),
                "deflate" => new ZLibStream(encoded, CompressionMode.Decompress, leaveOpen: true),
                "br" => new CompleteBrotliStream(encoded),
                _ => encoded
            };
            await using var bounded = new LimitedRead(decoded, MaximumBytes, "calendar_decoded_bytes", async (length, bytes) =>
            {
                integrity.Decoded(bytes);
                await VerifyAsync(request, link, permission, token);
                if (encoding is not (null or "identity") && length > 1024 * 1024 && length > encoded.Count * 100)
                    throw new CalendarSubscriptionException("calendar_expansion_ratio");
            });
            var snapshot = await stager.StageAsync(bounded, "calendar.ics", token);
            try
            {
                var remainder = new byte[4096];
                while (await encoded.ReadAsync(remainder, token) != 0) await VerifyAsync(request, link, permission, token);
                integrity.Verify();
                await VerifyAsync(request, link, permission, token);
                if (snapshot.Length == 0) { await snapshot.DisposeAsync(); return new("failed", Error: "calendar_empty_response"); }
                var responseTag = response.Headers.ETag?.ToString();
                if (responseTag?.Length > 512) responseTag = null;
                return new("modified", snapshot, responseTag, response.Content.Headers.LastModified);
            }
            catch { await snapshot.DisposeAsync(); throw; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new("failed", Error: "calendar_timeout"); }
        catch (CalendarSubscriptionException error) { return new("failed", Error: error.Code); }
        catch (SocketException) { return new("failed", Error: "calendar_dns_failed"); }
        catch (UnauthorizedAccessException) { return new("failed", Error: "calendar_staging_unavailable"); }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or ArgumentException or InvalidOperationException or FormatException)
        { return new("failed", Error: FindFailure(error) ?? "calendar_transport"); }
    }

    private async ValueTask VerifyAsync(CalendarDownloadRequest request, CalendarLink link, string permission, CancellationToken token)
    {
        if (permission != PermissionFingerprint() || !options.CurrentValue.Sources.CalendarAllowedOrigins.Any(origin => CalendarLink.ValidateOrigin(origin) == link.Origin))
            throw new CalendarSubscriptionException("calendar_origin_not_allowed");
        if (!await authority.IsCurrentAsync(request.SourceId, request.Generation, token)) throw new CalendarSubscriptionException("calendar_source_changed");
    }
    private string PermissionFingerprint() => string.Join('\n', options.CurrentValue.Sources.CalendarAllowedOrigins.Order(StringComparer.Ordinal)) + "\n" + options.CurrentValue.Privacy.CanaryTarget;

    private async ValueTask<IPAddress[]> ResolveAsync(CalendarLink link, CancellationToken token)
    {
        var addresses = await dns.ResolveAsync(link.Uri.IdnHost, token);
        if (addresses.Length is < 1 or > 64 || addresses.Any(address => !CalendarLink.IsPublicAddress(address))) throw new CalendarSubscriptionException("calendar_address_refused");
        if (!Uri.TryCreate("tcp://" + options.CurrentValue.Privacy.CanaryTarget, UriKind.Absolute, out var canary)) throw new CalendarSubscriptionException("calendar_canary_invalid");
        var canaryAddresses = IPAddress.TryParse(canary.IdnHost.Trim('[', ']'), out var literal) ? [literal] : await dns.ResolveAsync(canary.IdnHost, token);
        if (string.Equals(canary.IdnHost, link.Uri.IdnHost, StringComparison.OrdinalIgnoreCase) || addresses.Any(address => canaryAddresses.Contains(address)))
            throw new CalendarSubscriptionException("calendar_canary_refused");
        return addresses;
    }
    private static int? RetryAfter(HttpResponseMessage response)
    {
        var value = response.Headers.RetryAfter;
        var seconds = value?.Delta?.TotalSeconds ?? (value?.Date - DateTimeOffset.UtcNow)?.TotalSeconds;
        return seconds is null ? null : (int)Math.Clamp(seconds.Value, 1, 86400);
    }
    private static string? FindFailure(Exception error)
    {
        for (Exception? item = error; item is not null; item = item.InnerException)
            if (item is CalendarSubscriptionException failure) return failure.Code;
        return null;
    }

    private abstract class ForwardingStream(Stream inner) : Stream
    {
        protected Stream Inner { get; } = inner;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => Inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => Inner.Flush();
        public override Task FlushAsync(CancellationToken token) => Inner.FlushAsync(token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        protected override void Dispose(bool disposing) { if (disposing) Inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => Inner.DisposeAsync();
    }
    private sealed class CheckedConnection(Stream inner, Func<CancellationToken, ValueTask> verify) : ForwardingStream(inner)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => Inner.ReadAsync(buffer, token);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { await verify(token); await Inner.WriteAsync(buffer, token); }
    }
    private sealed class LimitedRead(Stream inner, long maximum, string code, Func<long, ReadOnlyMemory<byte>, ValueTask>? check = null) : ForwardingStream(inner)
    {
        public long Count { get; private set; }
        public override bool CanWrite => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var read = await Inner.ReadAsync(buffer, token); Count += read;
            if (Count > maximum) throw new CalendarSubscriptionException(code);
            if (check is not null) await check(Count, buffer[..read]);
            return read;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => throw new NotSupportedException();
    }
}
