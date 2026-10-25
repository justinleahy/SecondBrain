using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Migrations;

namespace SecondBrain.Storage.Backup;

/// <summary>Locks an owned temporary directory for its whole lifetime, including streamed downloads.</summary>
public sealed class OperationWorkspace : IDisposable
{
    public const string MarkerName = ".secondbrain-workspace";
    private readonly FileStream marker;
    private readonly bool deleteOnDispose;
    private int disposed;
    private OperationWorkspace(string path, FileStream marker, bool deleteOnDispose = true)
    { Path = path; this.marker = marker; this.deleteOnDispose = deleteOnDispose; }
    public string Path { get; }

    public static OperationWorkspace Create(string parent, string kind)
    {
        if (kind is not ("backup" or "export" or "restore")) throw new ArgumentException("Unknown workspace kind.", nameof(kind));
        new ManagedMutationFiles(parent, requireSafeRoot: true).ValidateRoot();
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var id = Guid.NewGuid().ToString("N");
        var path = System.IO.Path.Combine(parent, kind + "-" + id);
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(System.IO.Path.Combine(path, MarkerName), new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.ReadWrite | FileShare.Delete,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            if (!TryLock(stream.SafeFileHandle)) throw new IOException("Workspace lock could not be acquired.");
            stream.Write(Magic(kind, id)); stream.Flush(true);
            MigrationFiles.SyncDirectory(path); MigrationFiles.SyncDirectory(parent);
            return new(path, stream);
        }
        catch
        {
            stream?.Dispose();
            Directory.Delete(path, true); throw;
        }
    }

    public static OperationWorkspace? TryClaim(string path, out bool active)
    {
        active = false;
        var name = System.IO.Path.GetFileName(path);
        var separator = name.IndexOf('-');
        if (separator < 0) return null;
        var kind = name[..separator]; var id = name[(separator + 1)..];
        if (kind is not ("backup" or "export" or "restore") || id.Length != 32 || !id.All(char.IsAsciiHexDigitLower)) return null;
        if (new DirectoryInfo(path).LinkTarget is not null) return null;
        try
        {
            using var parent = new ManagedMutationFiles(path, requireSafeRoot: true).Open(System.IO.Path.Combine(path, MarkerName), "workspace-cleanup");
            var read = parent.OpenSnapshotRead();
            try
            {
                if (!TryLock(read.Stream.SafeFileHandle)) { active = true; read.Dispose(); return null; }
                var expected = Magic(kind, id);
                if (read.Identity.Length > expected.Length) { read.Dispose(); return null; }
                var actual = new byte[(int)read.Identity.Length]; read.Stream.ReadExactly(actual);
                // An interrupted first write can leave a prefix, including an empty marker.
                if (!actual.AsSpan().SequenceEqual(expected.AsSpan(0, actual.Length))) { read.Dispose(); return null; }
                return new(path, read.Stream, deleteOnDispose: false);
            }
            catch { read.Dispose(); throw; }
        }
        catch (IOException) when (!File.Exists(System.IO.Path.Combine(path, MarkerName))) { return null; }
    }

    public void Dispose()
    {
        if (deleteOnDispose) Delete();
        else if (Interlocked.Exchange(ref disposed, 1) == 0) marker.Dispose();
    }

    public void Delete()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
        finally { marker.Dispose(); }
    }

    private static byte[] Magic(string kind, string id) => Encoding.ASCII.GetBytes("SecondBrain workspace v1\n" + kind + "\n" + id + "\n");
    internal static bool TryLock(SafeFileHandle handle)
    {
        if (Flock(checked((int)handle.DangerousGetHandle()), 2 | 4) == 0) return true;
        if (Marshal.GetLastPInvokeError() is 11 or 35) return false;
        throw new IOException("Workspace lock failed.");
    }
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int descriptor, int operation);
}
