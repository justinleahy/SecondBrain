using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace SecondBrain.Extractor;

public sealed record ExtractorServerOptions
{
    public string SocketPath { get; init; } = "/run/secondbrain/extractor.sock";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumClients { get; init; } = 8;

    public static ExtractorServerOptions Parse(string[] args)
    {
        var options = new ExtractorServerOptions();
        for (var position = 0; position < args.Length; position += 2)
        {
            if (position + 1 == args.Length)
            {
                throw new ArgumentException($"A value is required for {args[position]}.");
            }
            options = args[position] switch
            {
                "--socket" => options with { SocketPath = args[position + 1] },
                "--request-timeout-ms" => options with
                {
                    RequestTimeout = TimeSpan.FromMilliseconds(int.Parse(args[position + 1], CultureInfo.InvariantCulture))
                },
                "--max-clients" => options with
                {
                    MaximumClients = int.Parse(args[position + 1], CultureInfo.InvariantCulture)
                },
                _ => throw new ArgumentException($"Unknown extractor option {args[position]}.")
            };
        }
        options.Validate();
        return options;
    }

    internal void Validate()
    {
        if (!Path.IsPathFullyQualified(SocketPath)
            || RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(2)
            || MaximumClients is < 1 or > 64)
        {
            throw new ArgumentException("Use an absolute socket path, a timeout of 1 ms..120 s, and 1..64 clients.");
        }
    }
}

/// <summary>Bounded M0 ping and descriptor-probe service; real extraction lands in M1.</summary>
public sealed class ExtractorSocketServer(ExtractorServerOptions options, ILogger<ExtractorSocketServer> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        options.Validate();
        var activated = GetActivatedListener();
        var ownsPath = activated is null;
        using var listener = activated ?? BindListener(options.SocketPath);
        using var capacity = new SemaphoreSlim(options.MaximumClients);
        var clients = new List<Task>();
        logger.LogInformation("Extractor socket ready at {SocketPath}; activated: {Activated}", options.SocketPath, !ownsPath);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await capacity.WaitAsync(cancellationToken);
                Socket client;
                try
                {
                    client = await listener.AcceptAsync(cancellationToken);
                }
                catch
                {
                    capacity.Release();
                    throw;
                }
                clients.RemoveAll(task => task.IsCompleted);
                clients.Add(Task.Run(() =>
                {
                    using (client)
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        deadline.CancelAfter(options.RequestTimeout);
                        try
                        {
                            HandleConnection(client, deadline.Token);
                        }
                        catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException
                            or OperationCanceledException or ObjectDisposedException)
                        {
                            // Never include frame contents or exception messages: requests may later carry document metadata.
                            logger.LogWarning("Extractor client closed; reason: {Reason}", exception.GetType().Name);
                        }
                        finally
                        {
                            capacity.Release();
                        }
                    }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown cancels accept and every bounded client exchange.
        }
        finally
        {
            await Task.WhenAll(clients);
            if (ownsPath)
            {
                listener.Dispose();
                File.Delete(options.SocketPath);
            }
        }
    }

    public static void HandleConnection(Socket client, CancellationToken cancellationToken)
    {
        using var frame = UnixDescriptorTransport.Receive(client, cancellationToken);
        ExtractorResponse response;
        var responseId = "";
        try
        {
            var request = JsonSerializer.Deserialize<ExtractorRequest>(frame.Json, ExtractorProtocol.JsonOptions);
            if (request?.Id is { Length: <= 128 } id)
            {
                responseId = id;
            }
            response = request is null ? new ExtractorResponse("", false, Error: "invalid_request")
                : Process(request, frame.Descriptor);
        }
        catch (JsonException)
        {
            response = new ExtractorResponse("", false, Error: "invalid_json");
        }
        catch (InvalidDataException)
        {
            response = new ExtractorResponse(responseId, false, Error: "invalid_descriptor");
        }
        catch (IOException)
        {
            response = new ExtractorResponse(responseId, false, Error: "descriptor_read_failed");
        }
        UnixDescriptorTransport.Send(client, ExtractorProtocol.Serialize(response), cancellationToken: cancellationToken);
    }

    private static ExtractorResponse Process(ExtractorRequest request, SafeFileHandle? descriptor)
    {
        if (string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 128
            || string.IsNullOrWhiteSpace(request.Operation) || request.Operation.Length > 128)
        {
            return new ExtractorResponse("", false, Error: "invalid_request");
        }
        if (request.Version != ExtractorProtocol.Version)
        {
            return new ExtractorResponse(request.Id, false, Error: "unsupported_version");
        }
        if (request.Operation == "ping")
        {
            return descriptor is null ? new ExtractorResponse(request.Id, true, Result: "pong")
                : new ExtractorResponse(request.Id, false, Error: "unexpected_descriptor");
        }
        if (request.Operation != "probe_descriptor")
        {
            return new ExtractorResponse(request.Id, false, Error: "not_implemented");
        }
        if (descriptor is null)
        {
            return new ExtractorResponse(request.Id, false, Error: "descriptor_required");
        }

        UnixDescriptorTransport.RequireReadOnlyRegularFile(descriptor);
        if (RandomAccess.GetLength(descriptor) > ExtractorProtocol.MaximumProbeBytes)
        {
            return new ExtractorResponse(request.Id, false, Error: "probe_size_exceeded");
        }
        // Positional reads leave the shared open-file-description offset unchanged.
        var bytes = new byte[ExtractorProtocol.MaximumProbeBytes + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = RandomAccess.Read(descriptor, bytes.AsSpan(total), total);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        if (total > ExtractorProtocol.MaximumProbeBytes)
        {
            return new ExtractorResponse(request.Id, false, Error: "probe_size_exceeded");
        }
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, total)));
        return new ExtractorResponse(request.Id, true, Result: "descriptor_read", BytesRead: total, Sha256: digest);
    }

    private static Socket BindListener(string socketPath)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The extractor service requires Linux or macOS.");
        }
        if (!Directory.Exists(Path.GetDirectoryName(socketPath)))
        {
            throw new DirectoryNotFoundException("Provision the extractor socket directory before startup.");
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            // Existing paths are never removed: a running service or an attacker-owned path must fail startup.
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            socket.Listen(16);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static Socket? GetActivatedListener()
    {
        if (!OperatingSystem.IsLinux()
            || Environment.GetEnvironmentVariable("LISTEN_PID") != Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
        {
            return null;
        }
        if (Environment.GetEnvironmentVariable("LISTEN_FDS") != "1")
        {
            throw new InvalidOperationException("The extractor requires one systemd Unix stream listener.");
        }
        var socket = new Socket(new SafeSocketHandle(3, ownsHandle: true));
        try
        {
            UnixDescriptorTransport.PreventInheritance(socket.SafeHandle);
            if (socket.AddressFamily != AddressFamily.Unix || socket.SocketType != SocketType.Stream
                || socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.AcceptConnection) is not int value || value != 1)
            {
                throw new InvalidOperationException("systemd fd 3 must be a listening Unix stream socket (Accept=no).");
            }
            foreach (var name in new[] { "LISTEN_PID", "LISTEN_FDS", "LISTEN_FDNAMES" })
            {
                Environment.SetEnvironmentVariable(name, null);
            }
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

internal sealed class ExtractorWorker(ExtractorServerOptions options, ILogger<ExtractorSocketServer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        new ExtractorSocketServer(options, logger).RunAsync(stoppingToken);
}
