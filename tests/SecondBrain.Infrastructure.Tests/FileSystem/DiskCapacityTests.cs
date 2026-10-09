using SecondBrain.Infrastructure.FileSystem;
using SecondBrain.Infrastructure.Security;
using Xunit;

namespace SecondBrain.Infrastructure.Tests.FileSystem;

public sealed class DiskCapacityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "secondbrain-disk-" + Guid.NewGuid().ToString("N"));

    public DiskCapacityTests() => Directory.CreateDirectory(root);

    [Fact]
    public void LongestContainingMountWinsOnSegmentBoundaries()
    {
        var physical = UnixPath.Canonicalize(root);
        var data = Directory.CreateDirectory(Path.Combine(root, "srv", "data")).FullName;
        var mounts = new[] { "/", Path.Combine(physical, "srv"), Path.Combine(physical, "srv", "dat"), Path.Combine(physical, "srv", "data-other") };
        Assert.Equal(Path.Combine(physical, "srv"), DiskCapacity.SelectMount(data, mounts));
        // A separately mounted data root is chosen over every ancestor filesystem, including '/'.
        Assert.Equal(Path.Combine(physical, "srv", "data") + "/", DiskCapacity.SelectMount(data, mounts.Append(Path.Combine(physical, "srv", "data") + "/")));
    }

    [Fact]
    public void SymlinkedDataRootSelectsItsTargetFilesystem()
    {
        var physical = UnixPath.Canonicalize(root);
        Directory.CreateDirectory(Path.Combine(root, "mnt", "volume", "data"));
        Directory.CreateDirectory(Path.Combine(root, "link-parent"));
        var link = Path.Combine(root, "link-parent", "data");
        Directory.CreateSymbolicLink(link, Path.Combine(root, "mnt", "volume", "data"));
        var volume = Path.Combine(physical, "mnt", "volume");
        // The lexical location sits on a different (decoy) mount; only the physical path is authoritative.
        var mounts = new[] { "/", Path.Combine(physical, "link-parent"), volume };
        Assert.Equal(volume, DiskCapacity.SelectMount(link, mounts));
        // A data root that is not created yet resolves through its existing, symlinked ancestors.
        Assert.Equal(volume, DiskCapacity.SelectMount(Path.Combine(link, "state", "not-yet"), mounts));
    }

    [Fact]
    public void NoContainingMountIsUnknownRatherThanTheRootFilesystem()
    {
        var physical = UnixPath.Canonicalize(root);
        Assert.Null(DiskCapacity.SelectMount(root, [Path.Combine(physical, "elsewhere"), "relative/mount"]));
        Assert.Throws<ArgumentException>(() => DiskCapacity.SelectMount("relative/path", ["/"]));
    }

    [Fact]
    public void RealMountTableReportsCapacityForAnExistingDataRoot()
    {
        Assert.True(new DiskCapacity().AvailableBytes(root) is > 0);
        Assert.Null(new DiskCapacity().AvailableBytes("relative/path"));
    }

    [Fact]
    public void ConcurrentProbesNeverReportUnknownCapacity()
    {
        var disk = new DiskCapacity();
        var unknown = 0;
        Parallel.For(0, 512, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            if (disk.AvailableBytes(root) is null) Interlocked.Increment(ref unknown);
        });
        Assert.Equal(0, unknown);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
