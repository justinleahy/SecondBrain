using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SecondBrain.Extractor;
using Xunit;

namespace SecondBrain.Deploy.Tests;

public sealed class ExtractorTests
{
    [Fact]
    public async Task PingUsesVersionedLengthPrefixedJson()
    {
        var response = await ExchangeAsync(new ExtractorRequest("ping-test", "ping"));
        Assert.True(response.Ok);
        Assert.Equal("ping-test", response.Id);
        Assert.Equal(1, response.Version);
        Assert.Equal("pong", response.Result);
    }

    [Fact]
    public async Task DescriptorPassingReadsUnlinkedFileAndPreservesSenderOffset()
    {
        using var file = TemporaryFile.Create("descriptor-only contents\n");
        using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(3, SeekOrigin.Begin);
        File.Delete(file.Path);

        var response = await ExchangeAsync(new ExtractorRequest("fd-spike", "probe_descriptor"), stream.SafeFileHandle);

        Assert.True(response.Ok);
        Assert.Equal("descriptor_read", response.Result);
        Assert.Equal(file.Bytes.Length, response.BytesRead);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(file.Bytes)), response.Sha256);
        Assert.Equal(file.Bytes[3], stream.ReadByte());
        Assert.False(stream.SafeFileHandle.IsClosed);
    }

    [Fact]
    public async Task ReceivedFrameOwnsAndClosesItsDescriptor()
    {
        using var file = TemporaryFile.Create("owned descriptor");
        using var original = File.OpenHandle(file.Path, FileMode.Open, FileAccess.Read);
        using var pair = SocketPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receiving = Task.Run(() => UnixDescriptorTransport.Receive(pair.Server, timeout.Token));
        UnixDescriptorTransport.Send(pair.Client, "{}"u8, original, timeout.Token);
        var frame = await receiving;
        Assert.NotNull(frame.Descriptor);
        Assert.Equal(file.Bytes.Length, RandomAccess.GetLength(frame.Descriptor));
        frame.Dispose();
        Assert.True(frame.Descriptor.IsClosed);
        Assert.False(original.IsClosed);
    }

    [Fact]
    public async Task ReceivedDescriptorHasCloseOnExecFlag()
    {
        using var file = TemporaryFile.Create("descriptor must not survive exec");
        using var original = File.OpenHandle(file.Path, FileMode.Open, FileAccess.Read);
        using var pair = SocketPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receiving = Task.Run(() => UnixDescriptorTransport.Receive(pair.Server, timeout.Token));
        UnixDescriptorTransport.Send(pair.Client, "{}"u8, original, timeout.Token);
        using var frame = await receiving;
        Assert.NotNull(frame.Descriptor);
        var flags = NativeDescriptorFlags.Get(checked((int)frame.Descriptor.DangerousGetHandle()), 1);
        Assert.True(flags >= 0, "F_GETFD must inspect the actual received descriptor.");
        Assert.Equal(1, flags & 1);
    }

    [Fact]
    public async Task ProbeRequiresDescriptor()
    {
        var response = await ExchangeAsync(new ExtractorRequest("missing", "probe_descriptor"));
        Assert.False(response.Ok);
        Assert.Equal("descriptor_required", response.Error);
    }

    [Fact]
    public async Task ProbeRejectsWritableDescriptor()
    {
        using var file = TemporaryFile.Create("must be read-only");
        using var writable = File.OpenHandle(file.Path, FileMode.Open, FileAccess.ReadWrite);
        var response = await ExchangeAsync(new ExtractorRequest("writable", "probe_descriptor"), writable);
        Assert.False(response.Ok);
        Assert.Equal("writable", response.Id);
        Assert.Equal("invalid_descriptor", response.Error);
    }

    [Fact]
    public async Task ProbeRejectsOversizedFileBeforeReading()
    {
        using var file = TemporaryFile.Create(new string('x', ExtractorProtocol.MaximumProbeBytes + 1));
        using var descriptor = File.OpenHandle(file.Path, FileMode.Open, FileAccess.Read);
        var response = await ExchangeAsync(new ExtractorRequest("large", "probe_descriptor"), descriptor);
        Assert.False(response.Ok);
        Assert.Equal("probe_size_exceeded", response.Error);
        Assert.Null(response.BytesRead);
    }

    [Fact]
    public async Task PingRejectsUnexpectedDescriptor()
    {
        using var file = TemporaryFile.Create("not a ping input");
        using var descriptor = File.OpenHandle(file.Path, FileMode.Open, FileAccess.Read);
        var response = await ExchangeAsync(new ExtractorRequest("ping-fd", "ping"), descriptor);
        Assert.False(response.Ok);
        Assert.Equal("unexpected_descriptor", response.Error);
    }

    [Theory]
    [InlineData("{", "invalid_json")]
    [InlineData("{\"id\":\"ping\",\"operation\":\"ping\",\"path\":\"/etc/passwd\"}", "invalid_json")]
    [InlineData("{\"operation\":\"ping\"}", "invalid_request")]
    [InlineData("null", "invalid_request")]
    public async Task InvalidRequestReceivesBoundedStructuredError(string json, string expected)
    {
        var response = await ExchangeBytesAsync(Encoding.UTF8.GetBytes(json));
        Assert.False(response.Ok);
        Assert.Equal(expected, response.Error);
    }

    [Fact]
    public async Task UnsupportedVersionIsRefused()
    {
        var response = await ExchangeAsync(new ExtractorRequest("version", "ping", Version: 2));
        Assert.False(response.Ok);
        Assert.Equal("unsupported_version", response.Error);
    }

    [Fact]
    public async Task RealExtractionRemainsExplicitM1Stub()
    {
        var response = await ExchangeAsync(new ExtractorRequest("stub", "extract"));
        Assert.False(response.Ok);
        Assert.Equal("not_implemented", response.Error);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(65537u)]
    [InlineData(uint.MaxValue)]
    public async Task FrameLengthIsRejectedBeforeBodyAllocation(uint length)
    {
        using var pair = SocketPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, length);
        var receiving = Task.Run(() => UnixDescriptorTransport.Receive(pair.Server, timeout.Token));
        pair.Client.Send(prefix);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await receiving);
    }

    [Fact]
    public async Task TransportReassemblesFragmentedFrame()
    {
        using var pair = SocketPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var json = ExtractorProtocol.Serialize(new ExtractorRequest("fragment", "ping"));
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)json.Length);
        var receiving = Task.Run(() => UnixDescriptorTransport.Receive(pair.Server, timeout.Token));
        foreach (var value in prefix.Concat(json))
        {
            pair.Client.Send(new[] { value });
        }
        using var frame = await receiving;
        Assert.Equal(json, frame.Json);
        Assert.Null(frame.Descriptor);
    }

    [Fact]
    public async Task TruncatedFrameIsRejected()
    {
        using var pair = SocketPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receiving = Task.Run(() => UnixDescriptorTransport.Receive(pair.Server, timeout.Token));
        pair.Client.Send(new byte[] { 0, 0, 0, 10, (byte)'{' });
        pair.Client.Shutdown(SocketShutdown.Send);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await receiving);
    }

    [Fact]
    public async Task CancellationBoundsIdleClient()
    {
        using var pair = SocketPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Run(() => UnixDescriptorTransport.Receive(pair.Server, timeout.Token)));
    }

    [Fact]
    public async Task StandaloneServerServesPingAndRemovesSocketOnShutdown()
    {
        var path = NewSocketPath();
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = new ExtractorServerOptions { SocketPath = path };
        var server = new ExtractorSocketServer(options, NullLogger<ExtractorSocketServer>.Instance);
        var running = server.RunAsync(shutdown.Token);
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(path), shutdown.Token);
        UnixDescriptorTransport.Send(client, ExtractorProtocol.Serialize(new ExtractorRequest("host", "ping")),
            cancellationToken: shutdown.Token);
        using var response = UnixDescriptorTransport.Receive(client, shutdown.Token);
        Assert.Equal("pong", JsonSerializer.Deserialize<ExtractorResponse>(response.Json, ExtractorProtocol.JsonOptions)!.Result);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite,
                File.GetUnixFileMode(path));
        }
        shutdown.Cancel();
        await running;
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ExistingPathIsPreservedAndRefusesStartup()
    {
        using var file = TemporaryFile.Create("existing path is never deleted");
        var options = new ExtractorServerOptions { SocketPath = file.Path };
        var server = new ExtractorSocketServer(options, NullLogger<ExtractorSocketServer>.Instance);
        await Assert.ThrowsAsync<SocketException>(() => server.RunAsync(CancellationToken.None));
        Assert.Equal(file.Bytes, File.ReadAllBytes(file.Path));
    }

    [Fact]
    public async Task MalformedClientCannotStopSocketServer()
    {
        var path = NewSocketPath();
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new ExtractorSocketServer(new ExtractorServerOptions { SocketPath = path, MaximumClients = 1 },
            NullLogger<ExtractorSocketServer>.Instance);
        var running = server.RunAsync(shutdown.Token);
        using (var malformed = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            await malformed.ConnectAsync(new UnixDomainSocketEndPoint(path), shutdown.Token);
            malformed.Send(new byte[] { 0xff, 0xff, 0xff, 0xff });
        }
        using var ping = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await ping.ConnectAsync(new UnixDomainSocketEndPoint(path), shutdown.Token);
        UnixDescriptorTransport.Send(ping, ExtractorProtocol.Serialize(new ExtractorRequest("after-error", "ping")),
            cancellationToken: shutdown.Token);
        using var response = UnixDescriptorTransport.Receive(ping, shutdown.Token);
        Assert.True(JsonSerializer.Deserialize<ExtractorResponse>(response.Json, ExtractorProtocol.JsonOptions)!.Ok);
        shutdown.Cancel();
        await running;
    }

    [Theory]
    [InlineData("--socket", "relative.sock")]
    [InlineData("--max-clients", "0")]
    [InlineData("--max-clients", "65")]
    [InlineData("--request-timeout-ms", "0")]
    [InlineData("--request-timeout-ms", "120001")]
    [InlineData("--unknown", "value")]
    public void CommandLineRejectsInvalidBounds(string name, string value) =>
        Assert.Throws<ArgumentException>(() => ExtractorServerOptions.Parse(new[] { name, value }));

    private static Task<ExtractorResponse> ExchangeAsync(ExtractorRequest request,
        Microsoft.Win32.SafeHandles.SafeFileHandle? descriptor = null) =>
        ExchangeBytesAsync(ExtractorProtocol.Serialize(request), descriptor);

    private static async Task<ExtractorResponse> ExchangeBytesAsync(byte[] json,
        Microsoft.Win32.SafeHandles.SafeFileHandle? descriptor = null)
    {
        using var pair = SocketPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handling = Task.Run(() => ExtractorSocketServer.HandleConnection(pair.Server, timeout.Token));
        UnixDescriptorTransport.Send(pair.Client, json, descriptor, timeout.Token);
        using var response = UnixDescriptorTransport.Receive(pair.Client, timeout.Token);
        await handling;
        Assert.Null(response.Descriptor);
        return JsonSerializer.Deserialize<ExtractorResponse>(response.Json, ExtractorProtocol.JsonOptions)!;
    }

    private static string NewSocketPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sbex-{Guid.NewGuid():N}");

    private sealed class SocketPair(Socket listener, Socket client, Socket server, string path) : IDisposable
    {
        public Socket Client { get; } = client;
        public Socket Server { get; } = server;

        public static SocketPair Create()
        {
            var path = NewSocketPath();
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(path));
            return new SocketPair(listener, client, listener.Accept(), path);
        }

        public void Dispose()
        {
            Server.Dispose();
            Client.Dispose();
            listener.Dispose();
            File.Delete(path);
        }
    }

    private sealed class TemporaryFile(string path, byte[] bytes) : IDisposable
    {
        public string Path { get; } = path;
        public byte[] Bytes { get; } = bytes;

        public static TemporaryFile Create(string contents)
        {
            var path = NewSocketPath();
            var bytes = Encoding.UTF8.GetBytes(contents);
            File.WriteAllBytes(path, bytes);
            return new TemporaryFile(path, bytes);
        }

        public void Dispose() => File.Delete(Path);
    }

    private static class NativeDescriptorFlags
    {
        // F_GETFD consumes no variadic arguments, providing an independent kernel readback of the setter.
        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        public static extern int Get(int descriptor, int command);
    }
}
