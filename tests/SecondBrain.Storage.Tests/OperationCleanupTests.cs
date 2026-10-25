using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Storage.Backup;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class OperationCleanupTests
{
    [Fact]
    public async Task CleansOnlyAbandonedOwnedWorkspacesAndNeverFollowsTheirLinks()
    {
        using var root = new MigrationTestDataRoot(); await using var state = new SqliteStateStore(root.Path);
        var staging = Directory.CreateDirectory(Path.Combine(root.Path, "staging")).FullName;
        using var active = OperationWorkspace.Create(staging, "export");
        var abandoned = Abandoned(staging, "backup"); await File.WriteAllTextAsync(Path.Combine(abandoned, "private.txt"), "discarded");
        var outside = Directory.CreateDirectory(Path.Combine(root.Path, "outside")).FullName; await File.WriteAllTextAsync(Path.Combine(outside, "retained.txt"), "retained");
        Directory.CreateSymbolicLink(Path.Combine(abandoned, "linked"), outside);
        var foreign = Directory.CreateDirectory(Path.Combine(staging, "export-" + Guid.NewGuid().ToString("N"))).FullName;
        await File.WriteAllTextAsync(Path.Combine(foreign, "unowned.txt"), "unowned");
        var result = await Service(state, root.Path).RunAsync();
        Assert.Equal(1, result.Workspaces); Assert.True(Directory.Exists(active.Path)); Assert.True(Directory.Exists(foreign));
        Assert.False(Directory.Exists(abandoned)); Assert.Equal("retained", await File.ReadAllTextAsync(Path.Combine(outside, "retained.txt")));
    }

    [Fact]
    public async Task CleanupIsBoundedAndRecognizesInterruptedMarkerWrites()
    {
        using var root = new MigrationTestDataRoot(); await using var state = new SqliteStateStore(root.Path);
        var staging = Directory.CreateDirectory(Path.Combine(root.Path, "staging")).FullName;
        for (var i = 0; i < 20; i++) Abandoned(staging, "restore", partial: i % 2 == 0);
        var service = Service(state, root.Path);
        var first = await service.RunAsync(); Assert.Equal(16, first.Workspaces); Assert.True(first.MoreWork);
        var second = await service.RunAsync(); Assert.Equal(4, second.Workspaces); Assert.False(second.MoreWork);
    }

    [Fact]
    public void ClaimDisposalWithoutDeletePreservesTheWorkspace()
    {
        using var root = new MigrationTestDataRoot();
        var staging = Directory.CreateDirectory(Path.Combine(root.Path, "staging")).FullName;
        var path = Abandoned(staging, "export");
        using (var claim = OperationWorkspace.TryClaim(path, out var active)) { Assert.False(active); Assert.NotNull(claim); }
        Assert.True(Directory.Exists(path));
        using var next = OperationWorkspace.TryClaim(path, out _); next!.Delete(); Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task ExpiresOnlyOldClosedDebugLogsAndPreservesOtherLogs()
    {
        using var root = new MigrationTestDataRoot(); await using var state = new SqliteStateStore(root.Path);
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        var logs = Directory.CreateDirectory(Path.Combine(root.Path, "logs")).FullName;
        var old = Log(logs, clock.GetUtcNow().AddDays(-2));
        var recent = Log(logs, clock.GetUtcNow());
        var busy = Log(logs, clock.GetUtcNow().AddDays(-3));
        await File.WriteAllTextAsync(Path.Combine(logs, "server.jsonl"), "ordinary log");
        using (var handle = File.OpenRead(busy))
        {
            Assert.Equal(0, Flock(checked((int)handle.SafeFileHandle.DangerousGetHandle()), 2 | 4));
            var first = await Service(state, root.Path, clock).RunAsync();
            Assert.Equal(1, first.DebugLogs); Assert.False(File.Exists(old)); Assert.True(File.Exists(busy));
        }
        Assert.Equal(1, (await Service(state, root.Path, clock).RunAsync()).DebugLogs);
        Assert.True(File.Exists(recent)); Assert.True(File.Exists(Path.Combine(logs, "server.jsonl")));
    }

    private static string Log(string directory, DateTimeOffset at)
    {
        var path = Path.Combine(directory, "debug-" + at.UtcDateTime.ToString("yyyyMMddTHHmmssfffffffZ") + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, "{}\n"); File.SetLastWriteTimeUtc(path, at.UtcDateTime); return path;
    }
    private static string Abandoned(string parent, string kind, bool partial = false)
    {
        var id = Guid.NewGuid().ToString("N"); var path = Directory.CreateDirectory(Path.Combine(parent, kind + "-" + id)).FullName;
        File.WriteAllBytes(Path.Combine(path, OperationWorkspace.MarkerName), partial ? [] : Encoding.ASCII.GetBytes("SecondBrain workspace v1\n" + kind + "\n" + id + "\n"));
        return path;
    }
    private static OperationCleanup Service(IStateStore state, string root, TimeProvider? clock = null) =>
        new(state, Options.Create(new SecondBrainOptions { DataRoot = root }), clock ?? TimeProvider.System);
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)] private static extern int Flock(int descriptor, int operation);
}
