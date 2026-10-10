using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Infrastructure.Ingestion;

/// <summary>Same-filesystem atomic inbox claims remain visible in .claimed until successful finalization.</summary>
public sealed class InboxClaims(InputStager stager) : IInboxClaims
{
    public ValueTask<string> EnsureAsync(string incomingRoot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = stager.RetainRoot(incomingRoot);
        // Production provisions this child before mounting it writable beneath the
        // read-only incoming tree. Development roots may create it on first use.
        using var inbox = OpenOrCreate(root, "inbox");
        var path = Path.Combine(incomingRoot, "inbox"); stager.RetainOwnedRoot(path, inbox);
        return ValueTask.FromResult(path);
    }

    public ValueTask<ClaimedInput> ClaimAsync(string root, string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (relativePath.Length is < 1 or > 180 || relativePath.Contains('/') || relativePath.Contains('\\') || relativePath is "." or ".." || relativePath.Any(char.IsControl)) throw new InvalidDataException("inbox_path");
        var descriptor = stager.RetainRoot(root);
        using var source = InputStager.OpenChild(descriptor, relativePath);
        InputStager.RequireRegularSingleLink(source);
        var fd = checked((int)descriptor.DangerousGetHandle());
        if (MkdirAt(fd, ".claimed", 448) != 0 && Marshal.GetLastPInvokeError() != 17) throw new IOException("inbox_claim_directory");
        using var claims = InputStager.OpenAt(descriptor, ".claimed", 0x10000);
        InputStager.RequireOwnedDirectory(claims, privateOnly: true);
        var name = Guid.NewGuid().ToString("N") + "--" + relativePath;
        if (RenameAt2(fd, relativePath, checked((int)claims.DangerousGetHandle()), name, 1) != 0) throw new IOException("inbox_claim_failed");
        if (Fsync(fd) != 0 || Fsync(checked((int)claims.DangerousGetHandle())) != 0) throw new IOException("inbox_claim_flush");
        // Recheck the actual renamed entry; replacements remain visibly claimed on any failure.
        using var claimed = InputStager.OpenChild(claims, name); InputStager.RequireRegularSingleLink(claimed);
        return ValueTask.FromResult(new ClaimedInput(".claimed/" + name, relativePath));
    }
    public async IAsyncEnumerable<ClaimedInput> EnumerateAsync(string root, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var descriptor = stager.RetainRoot(root);
        Microsoft.Win32.SafeHandles.SafeFileHandle claims;
        try { claims = InputStager.OpenAt(descriptor, ".claimed", 0x10000); } catch (IOException) { yield break; }
        using (claims)
        {
            InputStager.RequireOwnedDirectory(claims, privateOnly: true);
            var count = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries($"/proc/self/fd/{claims.DangerousGetHandle()}"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > 500000) throw new InvalidDataException("inbox_claim_count");
                var name = Path.GetFileName(path);
                if (name.Length > 34 && name[32..34] == "--" && Guid.TryParseExact(name[..32], "N", out _)) yield return new(".claimed/" + name, name[34..]);
            }
        }
        await Task.CompletedTask;
    }
    public ValueTask CompleteAsync(string root, ClaimedInput claim, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!claim.RelativeClaimPath.StartsWith(".claimed/", StringComparison.Ordinal)) throw new InvalidDataException("inbox_claim_path");
        var name = claim.RelativeClaimPath[9..];
        if (name.Contains('/') || name is "" or "." or "..") throw new InvalidDataException("inbox_claim_path");
        using var claims = InputStager.OpenAt(stager.RetainRoot(root), ".claimed", 0x10000);
        InputStager.RequireOwnedDirectory(claims, privateOnly: true);
        if (UnlinkAt(checked((int)claims.DangerousGetHandle()), name, 0) != 0 && Marshal.GetLastPInvokeError() != 2) throw new IOException("inbox_claim_cleanup");
        return ValueTask.CompletedTask;
    }
    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenOrCreate(Microsoft.Win32.SafeHandles.SafeFileHandle root, string name)
    {
        try { return InputStager.OpenAt(root, name, 0x10000); }
        catch (IOException exception) when (exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 })
        {
            if (MkdirAt(checked((int)root.DangerousGetHandle()), name, 448) != 0 && Marshal.GetLastPInvokeError() != 17) throw new IOException("inbox_directory");
            return InputStager.OpenAt(root, name, 0x10000);
        }
    }
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] private static extern int Fsync(int descriptor);
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)] private static extern int MkdirAt(int parent, string name, uint mode);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)] private static extern int RenameAt2(int from, string source, int to, string destination, uint flags);
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)] private static extern int UnlinkAt(int parent, string name, int flags);
}
