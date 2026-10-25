using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Sources;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Infrastructure.Network;
using Xunit;

namespace SecondBrain.Infrastructure.Tests.Network;

public sealed class CalendarDownloadTests
{
    [Theory]
    [InlineData("http://feed.test/calendar")]
    [InlineData("https://user:password@feed.test/calendar")]
    [InlineData("https://feed.test/calendar#secret")]
    [InlineData("https://feed.test./calendar")]
    [InlineData("https://feed.test\\@127.0.0.1/calendar")]
    [InlineData(" https://feed.test/calendar")]
    public void InvalidLinksFailWithoutEchoingInput(string url)
    {
        var failure = Assert.Throws<CalendarSubscriptionException>(() => CalendarLink.Parse(url));
        Assert.Equal("calendar_url_invalid", failure.Message);
    }

    [Fact]
    public void WebcalTranslationPreservesEscapedPathQueryAndPort()
    {
        var link = CalendarLink.Parse("webcal://feed.test:8443/published/a%2Fb?key=A%2FB&x=two");
        Assert.Equal("https://feed.test:8443/published/a%2Fb?key=A%2FB&x=two", link.Uri.OriginalString);
        Assert.Equal("https://feed.test:8443", link.ToString());
    }

    [Theory]
    [InlineData("127.0.0.1")][InlineData("10.0.0.1")][InlineData("172.16.0.1")][InlineData("192.168.0.1")]
    [InlineData("169.254.169.254")][InlineData("100.100.100.100")][InlineData("192.0.2.1")][InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")][InlineData("203.0.113.1")][InlineData("0.0.0.0")][InlineData("224.0.0.1")]
    [InlineData("::1")][InlineData("::")][InlineData("fe80::1")][InlineData("fc00::1")][InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")][InlineData("2001:db8::1")][InlineData("2002:7f00:1::")][InlineData("3fff::1")]
    public void NonPublicAddressesAreRejected(string address) => Assert.False(CalendarLink.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("93.184.215.14")][InlineData("2606:4700::6810:85e5")][InlineData("::ffff:93.184.215.14")]
    public void PublicUnicastAddressesAreAccepted(string address) => Assert.True(CalendarLink.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task ConditionalDownloadUsesOnlyCalendarHeadersAndPrivateReadOnlyStaging()
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = request =>
        {
            Assert.Equal("https://feed.test/no-extension?secret=synthetic", request.RequestUri!.OriginalString);
            Assert.Equal(HttpVersion.Version11, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            Assert.Equal("\"old\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
            Assert.NotNull(request.Headers.IfModifiedSince);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("BEGIN:VCALENDAR\nVERSION:2.0\nEND:VCALENDAR") };
            response.Headers.ETag = new("\"new\""); return response;
        };
        var result = await fixture.Transport.DownloadAsync(new("source", 2, "webcal://feed.test/no-extension?secret=synthetic", "\"old\"", DateTimeOffset.UnixEpoch));
        Assert.Equal("modified", result.Status); Assert.Equal("\"new\"", result.ETag);
        Assert.NotNull(result.Snapshot); Assert.False(result.Snapshot.Content.CanWrite);
        Assert.Equal("calendar.ics", result.Snapshot.Filename);
        Assert.Contains("BEGIN:VCALENDAR", await new StreamReader(result.Snapshot.Content).ReadToEndAsync());
        await result.Snapshot.DisposeAsync();
        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    [Theory]
    [InlineData(304, "not_modified", null)]
    [InlineData(302, "failed", "calendar_redirect_refused")]
    [InlineData(204, "failed", "calendar_empty_response")]
    [InlineData(401, "failed", "calendar_unavailable")]
    [InlineData(403, "failed", "calendar_unavailable")]
    [InlineData(404, "failed", "calendar_unavailable")]
    [InlineData(410, "failed", "calendar_unavailable")]
    [InlineData(429, "failed", "calendar_rate_limited")]
    [InlineData(503, "failed", "calendar_server_error")]
    public async Task StatusResponsesAreSanitizedAndNeverFollowed(int status, string expected, string? code)
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = _ => new((HttpStatusCode)status) { Headers = { Location = new("https://other.test/private"), RetryAfter = new(TimeSpan.FromDays(2)) } };
        var result = await fixture.Transport.DownloadAsync(Request());
        Assert.Equal(expected, result.Status); Assert.Equal(code, result.Error); Assert.Equal(1, fixture.Handler.Calls);
        if (status is 429 or 503) Assert.Equal(86400, result.RetryAfterSeconds);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task EmptyAndOversizedBodiesNeverBecomeSnapshots()
    {
        using var fixture = new Fixture();
        var empty = await fixture.Transport.DownloadAsync(Request());
        Assert.Equal("calendar_empty_response", empty.Error);
        fixture.Handler.Reply = _ => new(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream()) { Headers = { ContentLength = CalendarDownloadTransport.MaximumBytes + 1 } } };
        Assert.Equal("calendar_encoded_bytes", (await fixture.Transport.DownloadAsync(Request())).Error);
        fixture.Handler.Reply = _ => new(HttpStatusCode.OK) { Content = new StreamContent(new ZeroStream(CalendarDownloadTransport.MaximumBytes + 1)) };
        Assert.Equal("calendar_encoded_bytes", (await fixture.Transport.DownloadAsync(Request())).Error);
        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task CompressionHasIndependentExpansionBounds()
    {
        using var fixture = new Fixture();
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(new byte[2 * 1024 * 1024]);
        fixture.Handler.Reply = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) { Headers = { ContentEncoding = { "gzip" } } } };
        Assert.Equal("calendar_expansion_ratio", (await fixture.Transport.DownloadAsync(Request())).Error);
        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task RevocationDuringTheBodyDiscardsThePrivateStage()
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = _ => new(HttpStatusCode.OK) { Content = new StreamContent(new MutatingStream(Encoding.UTF8.GetBytes("calendar"), () => fixture.Authority.Current = false)) };
        Assert.Equal("calendar_source_changed", (await fixture.Transport.DownloadAsync(Request())).Error);
        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    [Theory]
    [InlineData("gzip", 0)][InlineData("gzip", 1)][InlineData("gzip", 4)][InlineData("gzip", 8)]
    [InlineData("deflate", 0)][InlineData("deflate", 1)][InlineData("deflate", 4)][InlineData("deflate", 8)]
    [InlineData("br", 0)][InlineData("br", 1)][InlineData("br", 4)][InlineData("br", 8)]
    public async Task CompressedSnapshotsRequireACompleteEnvelope(string encoding, int removedBytes)
    {
        using var fixture = new Fixture();
        using var compressed = new MemoryStream();
        const string text = "BEGIN:VCALENDAR\nVERSION:2.0\nEND:VCALENDAR\n";
        using (Stream compressor = encoding == "gzip" ? new GZipStream(compressed, CompressionLevel.SmallestSize, true) :
            encoding == "deflate" ? new ZLibStream(compressed, CompressionLevel.SmallestSize, true) : new BrotliStream(compressed, CompressionLevel.SmallestSize, true))
            compressor.Write(Encoding.UTF8.GetBytes(text));
        var bytes = compressed.ToArray();
        fixture.Handler.Reply = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes[..(bytes.Length - removedBytes)]) { Headers = { ContentEncoding = { encoding } } } };
        var result = await fixture.Transport.DownloadAsync(Request());
        if (removedBytes == 0)
        {
            Assert.Equal("modified", result.Status); Assert.NotNull(result.Snapshot);
            Assert.Equal(text, await new StreamReader(result.Snapshot.Content).ReadToEndAsync());
            await result.Snapshot.DisposeAsync();
        }
        else { Assert.Equal("failed", result.Status); Assert.Null(result.Snapshot); }
        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task BrotliCanFlushDecodedBytesAfterConsumingItsLastInputBuffer()
    {
        using var fixture = new Fixture(); using var encoded = new MemoryStream();
        using (var compressor = new BrotliStream(encoded, CompressionLevel.SmallestSize, true)) compressor.Write(new byte[200000]);
        fixture.Handler.Reply = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(encoded.ToArray()) { Headers = { ContentEncoding = { "br" } } } };
        var result = await fixture.Transport.DownloadAsync(Request());
        Assert.Equal("modified", result.Status); Assert.NotNull(result.Snapshot); Assert.Equal(200000, result.Snapshot.Length);
        await result.Snapshot.DisposeAsync();
    }

    [Fact]
    public async Task OriginAndSourceChangesStopTheRequestAfterDnsOrDuringStreaming()
    {
        using var fixture = new Fixture();
        fixture.Dns.BeforeResolve = () => fixture.Options.CurrentValue.Sources.CalendarAllowedOrigins.Clear();
        Assert.Equal("calendar_origin_not_allowed", (await fixture.Transport.DownloadAsync(Request())).Error);
        Assert.Equal(0, fixture.Handler.Calls);
        fixture.Options.CurrentValue.Sources.CalendarAllowedOrigins = ["https://feed.test"];
        fixture.Dns.BeforeResolve = null;
        fixture.Handler.Reply = _ => { fixture.Authority.Current = false; return new(HttpStatusCode.OK) { Content = new StringContent("calendar") }; };
        Assert.Equal("calendar_source_changed", (await fixture.Transport.DownloadAsync(Request())).Error);
    }

    [Fact]
    public async Task ProviderTrustDoesNotAuthorizeCalendarDownloadsAndCanaryIsNeverAllowed()
    {
        using var fixture = new Fixture();
        fixture.Options.CurrentValue.Sources.CalendarAllowedOrigins.Clear();
        fixture.Options.CurrentValue.Privacy.TrustedServices = ["https://feed.test"];
        Assert.Equal("calendar_origin_not_allowed", (await fixture.Transport.DownloadAsync(Request())).Error);
        Assert.Equal(0, fixture.Handler.Calls);
        fixture.Options.CurrentValue.Sources.CalendarAllowedOrigins = ["https://feed.test"];
        fixture.Dns.Addresses = [IPAddress.Parse("1.1.1.1")];
        Assert.Equal("calendar_canary_refused", (await fixture.Transport.DownloadAsync(Request())).Error);
    }

    [Fact]
    public async Task RebindingToLoopbackIsRejectedBeforeAnySocketReachesTheTarget()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var fixture = new Fixture();
        fixture.Options.CurrentValue.Sources.CalendarAllowedOrigins = ["https://feed.test:" + ((IPEndPoint)listener.LocalEndpoint).Port];
        var calls = 0;
        fixture.Dns.BeforeResolve = () => { if (++calls == 2) fixture.Dns.Addresses = [IPAddress.Loopback]; };
        var transport = new CalendarDownloadTransport(fixture.Options, fixture.Dns, fixture.Authority, fixture.Stager);
        var result = await transport.DownloadAsync(new("source", 2, fixture.Options.CurrentValue.Sources.CalendarAllowedOrigins[0] + "/secret"));
        Assert.Equal("calendar_address_refused", result.Error); Assert.True(calls >= 2); Assert.False(listener.Pending());
    }

    [Fact]
    public async Task DnsFailureRemainsSanitizedAndNeverSends()
    {
        using var fixture = new Fixture();
        fixture.Dns.BeforeResolve = () => throw new SocketException();
        Assert.Equal("calendar_dns_failed", (await fixture.Transport.DownloadAsync(Request())).Error);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task TransportErrorsDoNotEchoTheBearerLink()
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = request => throw new HttpRequestException("request failed: " + request.RequestUri);
        var result = await fixture.Transport.DownloadAsync(Request());
        Assert.Equal("calendar_transport", result.Error); Assert.DoesNotContain("secret", result.ToString());
        Assert.DoesNotContain("secret", Request().ToString());
    }

    private static CalendarDownloadRequest Request() => new("source", 2, "https://feed.test/no-extension?secret=synthetic");
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "calendar-download-" + Guid.NewGuid().ToString("N"));
        internal Monitor Options { get; } = new(); internal Resolver Dns { get; } = new(); internal Authority Authority { get; } = new();
        internal Handler Handler { get; } = new(); internal InputStager Stager { get; }
        internal CalendarDownloadTransport Transport { get; }
        internal Fixture() { Directory.CreateDirectory(Root); Stager = new(Root); Transport = new(Options, Dns, Authority, Stager, () => Handler); }
        public void Dispose() { Stager.Dispose(); Directory.Delete(Root, recursive: true); }
    }
    private sealed class Monitor : IOptionsMonitor<SecondBrainOptions>
    {
        public SecondBrainOptions CurrentValue { get; } = new() { Sources = new() { CalendarAllowedOrigins = ["https://feed.test"] } };
        public SecondBrainOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SecondBrainOptions, string?> listener) => null;
    }
    private sealed class Resolver : IDnsResolver
    {
        internal Action? BeforeResolve { get; set; } internal IPAddress[] Addresses { get; set; } = [IPAddress.Parse("93.184.215.14")];
        public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default) { BeforeResolve?.Invoke(); return ValueTask.FromResult(Addresses); }
    }
    private sealed class Authority : ICalendarFetchAuthority
    {
        internal bool Current { get; set; } = true;
        public ValueTask<bool> IsCurrentAsync(string id, long generation, CancellationToken token = default) => ValueTask.FromResult(Current);
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal Func<HttpRequestMessage, HttpResponseMessage> Reply { get; set; } = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return Task.FromResult(Reply(request)); }
    }
    private sealed class ZeroStream(long remaining) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var read = (int)Math.Min(count, remaining); Array.Clear(buffer, offset, read); remaining -= read; return read; }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class MutatingStream(byte[] bytes, Action afterRead) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var read = await base.ReadAsync(buffer, cancellationToken); if (read > 0) afterRead(); return read; }
    }
}
