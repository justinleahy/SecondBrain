using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SecondBrain.Extractor.Wire;

namespace SecondBrain.Extractor;

/// <summary>Disposable parser processes isolate hangs; the service keeps accepting requests after a parser fails.</summary>
public static class ExtractionSupervisor
{
    public static void Run(Socket client, ExtractorRequest request, SafeFileHandle descriptor, CancellationToken token) => Run(client, request, descriptor, token, null);

    /// <summary>Runs one request; <paramref name="workerFactory"/>, when given, starts a replacement worker for the socket path.</summary>
    public static void Run(Socket client, ExtractorRequest request, SafeFileHandle descriptor, CancellationToken token, Func<string, ProcessStartInfo>? workerFactory)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var directory = Path.Combine(Path.GetTempPath(), "sbparse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "worker.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path)); listener.Listen(1);
        var appHost = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, "SecondBrain.Extractor");
        var executable = File.Exists(appHost) ? appHost : "dotnet";
        var start = workerFactory?.Invoke(path) ?? new ProcessStartInfo { FileName = executable, UseShellExecute = false };
        start.WorkingDirectory = directory;
        if (workerFactory is null)
        {
            if (Path.GetFileNameWithoutExtension(executable) == "dotnet") start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("--parse-worker"); start.ArgumentList.Add(path);
        }
        start.Environment.Clear(); start.Environment["DOTNET_EnableDiagnostics"] = "0";
        start.Environment["TMPDIR"] = directory;
        start.Environment["DOTNET_GCHeapHardLimit"] = "10000000"; // hexadecimal 256 MiB, set before runtime startup.
        start.Environment["DOTNET_gcServer"] = "0";
        using var process = Process.Start(start) ?? throw new IOException();
        using var cancellation = token.Register(() => { try { KillGroup(process); } catch (InvalidOperationException) { } });
        try
        {
            using var worker = listener.AcceptAsync(token).AsTask().GetAwaiter().GetResult();
            UnixDescriptorTransport.Send(worker, ExtractorProtocol.Serialize(request), descriptor, token);
            for (var sequence = 0; sequence < 1024; sequence++)
            {
                using var frame = UnixDescriptorTransport.Receive(worker, token);
                if (frame.Descriptor is not null) throw new InvalidDataException();
                var chunk = JsonSerializer.Deserialize<ExtractionChunk>(frame.Json, ExtractorProtocol.JsonOptions) ?? throw new InvalidDataException();
                if (chunk.Id != request.Id || chunk.Sequence != sequence || chunk.Version != 2) throw new InvalidDataException();
                UnixDescriptorTransport.Send(client, frame.Json, cancellationToken: token);
                if (chunk.Final) return;
            }
            throw new InvalidDataException();
        }
        finally
        {
            try { if (!process.HasExited) KillGroup(process); process.WaitForExit(5000); }
            finally { Directory.Delete(directory, recursive: true); }
        }
    }

    private static void KillGroup(Process process)
    {
        // The child establishes its own session before accepting any parser input.
        if (!OperatingSystem.IsWindows()) NativeKill(-process.Id, 9);
        if (!process.HasExited) process.Kill(entireProcessTree: true);
    }
    [DllImport("libc", EntryPoint = "setsid", SetLastError = true)] private static extern int NewSession();
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int NativeKill(int pid, int signal);

    [StructLayout(LayoutKind.Sequential)]
    private struct ResourceLimit { public nuint Soft; public nuint Hard; }
    [DllImport("libc", EntryPoint = "setrlimit", SetLastError = true)]
    private static extern int SetResourceLimit(int resource, ref ResourceLimit limit);

    public static void Worker(string path)
    {
        // RLIMIT_CPU belongs to each disposable parser, never the long-lived supervisor.
        var cpu = new ResourceLimit { Soft = 120, Hard = 120 };
        if (SetResourceLimit(0, ref cpu) != 0) throw new IOException("Cannot bound parser CPU time.");
        if (NewSession() < 0) throw new IOException("Cannot isolate parser process group.");
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(path));
        using var frame = UnixDescriptorTransport.Receive(socket, token.Token);
        var request = JsonSerializer.Deserialize<ExtractorRequest>(frame.Json, ExtractorProtocol.JsonOptions) ?? throw new InvalidDataException();
        if (frame.Descriptor is null || request.Operation != "extract_v2") throw new InvalidDataException();
        var calendar = request.CalendarFrom is null ? null : new SecondBrain.Extraction.Contracts.CalendarWindow(
            request.CalendarFrom, request.CalendarTo ?? throw new InvalidDataException(), request.CalendarTimeZone ?? throw new InvalidDataException(), request.CalendarMaxOccurrences ?? 100000);
        var result = DocumentParser.Extract(frame.Descriptor, request.FormatHint, token.Token, calendar);
        ExtractorSocketServer.SendExtraction(socket, request.Id, result, token.Token);
    }
}
