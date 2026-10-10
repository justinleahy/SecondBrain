using System.Diagnostics;
using System.Net.Sockets;
using SecondBrain.Extractor;
using Xunit;

namespace SecondBrain.Extractor.Tests;

public sealed class SupervisorTests
{
    [Fact]
    public async Task TimeoutKillsHungWorkerAndItsDescendant()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sb-supervisor-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var socketPath = Path.Combine(directory, "client.sock"); var report = Path.Combine(directory, "pids");
        var script = Path.Combine(directory, "hang.py"); var file = Path.Combine(directory, "input.txt");
        await File.WriteAllTextAsync(file, "bounded descriptor input");
        await File.WriteAllTextAsync(script, """
            import os, socket, subprocess, sys, time
            os.setsid()
            child = subprocess.Popen(['/usr/bin/sleep', '60'])
            with open(sys.argv[2], 'w') as f: f.write(str(os.getpid()) + '\n' + str(child.pid))
            s = socket.socket(socket.AF_UNIX)
            s.connect(sys.argv[1])
            time.sleep(60)
            """);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath)); listener.Listen(1);
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath)); using var server = await listener.AcceptAsync();
        using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            var running = Task.Run(() => ExtractionSupervisor.Run(server, new("timeout", "extract_v2", FormatHint: ".txt"), handle, timeout.Token, path =>
            {
                var start = new ProcessStartInfo("/usr/bin/python3"); start.ArgumentList.Add(script); start.ArgumentList.Add(path); start.ArgumentList.Add(report); return start;
            }));
            try { await running; Assert.Fail("Hung worker should fail on deadline."); }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException) { }
            Assert.True(File.Exists(report), "The real worker and child must start before the deadline.");
            var pids = (await File.ReadAllLinesAsync(report)).Select(int.Parse).ToArray(); Assert.Equal(2, pids.Length);
            foreach (var pid in pids)
            {
                var until = Stopwatch.StartNew();
                while (IsRunning(pid) && until.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
                Assert.False(IsRunning(pid), $"Parser process {pid} survived the group deadline.");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public async Task DisposableParserHasKernelCpuLimitBeforeReceivingInput()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "sb-cpu-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "worker.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path)); listener.Listen(1);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute=false };
        start.ArgumentList.Add(typeof(SecondBrain.Extractor.Program).Assembly.Location);
        start.ArgumentList.Add("--parse-worker"); start.ArgumentList.Add(path);
        using var process = Process.Start(start)!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var peer = await listener.AcceptAsync(timeout.Token);
            var limits = await File.ReadAllLinesAsync($"/proc/{process.Id}/limits");
            var cpu = Assert.Single(limits, line => line.StartsWith("Max cpu time", StringComparison.Ordinal));
            Assert.Equal(new[] { "Max", "cpu", "time", "120", "120", "seconds" }, cpu.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree:true);
            await process.WaitForExitAsync();
            Directory.Delete(directory, recursive:true);
        }
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
