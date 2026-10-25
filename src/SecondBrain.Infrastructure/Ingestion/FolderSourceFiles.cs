using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Sources;

namespace SecondBrain.Infrastructure.Ingestion;

/// <summary>Bounded descriptor-rooted scans; stager rechecks containment before any content is consumed.</summary>
public sealed class FolderSourceFiles(IInputStager stager, IExtractorClient? extractor = null, bool purgeScan = false) : IFolderSourceFiles
{
    public async IAsyncEnumerable<string> WatchAsync(FolderSourceConfiguration configuration, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var changes = Channel.CreateBounded<string>(new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        using var watcher = new FileSystemWatcher(configuration.Path) { IncludeSubdirectories = configuration.Recursive, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 16 * 1024 };
        void Changed(object sender, FileSystemEventArgs args)
        {
            var name = args.Name?.Replace('\\', '/') ?? "";
            if (name.StartsWith(".claimed/", StringComparison.Ordinal)) return;
            changes.Writer.TryWrite(name);
        }
        watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
        watcher.Renamed += (sender, args) => { Changed(sender, args); changes.Writer.TryWrite(args.OldName ?? ""); };
        watcher.Error += (_, _) => changes.Writer.TryWrite("");
        watcher.EnableRaisingEvents = true;
        await foreach (var path in changes.Reader.ReadAllAsync(cancellationToken)) yield return path;
    }

    public async IAsyncEnumerable<SourceFileEntry> ScanAsync(FolderSourceConfiguration configuration, bool deep, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var pending = new Queue<string>(); pending.Enqueue(""); var entries = 0;
        // The source root is pinned by InputStager before scans begin.
        using var root = stager is InputStager concrete ? Duplicate(concrete.RetainRoot(configuration.Path)) : Open(configuration.Path);
        var ignore = purgeScan ? [] : await LoadIgnoreAsync(configuration.Path, cancellationToken);
        var include = configuration.Include.Select(SourceGlob.Compile).ToArray();
        var exclude = configuration.Exclude.Select(SourceGlob.Compile).ToArray();
        while (pending.TryDequeue(out var relative))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var directory = relative.Length == 0 ? null : OpenRelative(root, relative, directory: true);
            var handle = directory ?? root;
            var descriptorPath = $"/proc/self/fd/{handle.DangerousGetHandle()}";
            foreach (var candidate in Directory.EnumerateFileSystemEntries(descriptorPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > 500000) throw new InvalidDataException("source_files");
                var name = Path.GetFileName(candidate);
                if (!purgeScan && (name is ".claimed" or ".brainignore" || name.StartsWith(".sb-", StringComparison.Ordinal))) continue;
                var child = relative.Length == 0 ? name : relative + "/" + name;
                SafeFileHandle? opened;
                // A vanished entry (ENOENT) or a symlink (ELOOP) is not present; any other open failure is unreadable.
                try { opened = OpenRelative(handle, name, directory: false); }
                catch (IOException exception) when (exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }) { continue; }
                catch (IOException exception) when (exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 40 })
                {
                    if (!purgeScan || !Matches(child, include, exclude)) continue;
                    opened = null;
                }
                catch (IOException) { opened = null; }
                if (opened is null)
                {
                    if (!IsIgnored(child, ignore)) yield return SourceFileEntry.UnreadableEntry(child);
                    continue;
                }
                SourceFileEntry? result = null;
                using (opened)
                {
                    var buffer = Marshal.AllocHGlobal(256);
                    try
                    {
                        if (Fstat(opened, buffer) != 0) result = SourceFileEntry.UnreadableEntry(child);
                        else
                        {
                            var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
                            var mode = unchecked((uint)Marshal.ReadInt32(buffer, arm ? 16 : 24)) & 0xf000;
                            if (mode == 0x4000)
                            {
                                if (purgeScan && Matches(child, include, exclude))
                                { result = SourceFileEntry.UnreadableEntry(child); }
                                else
                                {
                                if (purgeScan && child.Count(c => c == '/') >= 64) throw new InvalidDataException("purge_directory_depth");
                                if (configuration.Recursive && child.Count(c => c == '/') < 64) pending.Enqueue(child);
                                continue;
                                }
                            }
                            if (!Matches(child, include, exclude) || IsIgnored(child, ignore)) continue;
                            if (mode != 0x8000)
                            {
                                if (purgeScan) result = SourceFileEntry.UnreadableEntry(child);
                                else continue;
                            }
                            if (mode == 0x8000)
                            {
                                var length = RandomAccess.GetLength(opened);
                                var modified = InputStager.StatModifiedAt(buffer);
                                string? hash = null;
                                try
                                {
                                    // The staging policy is judged from this descriptor's stat alone, so shallow scans name it too.
                                    InputStager.RequireRegularSingleLink(opened);
                                    if (InputStager.NameRejection(child) is { } invalid) throw new InvalidDataException(invalid);
                                    if (deep) { await using var stage = await stager.OpenSourceAsync(configuration.Path, child, cancellationToken); hash = stage.Sha256; }
                                    result = new(child, length, modified, hash);
                                }
                                catch (IOException) { result = SourceFileEntry.UnreadableEntry(child); }
                                // A staging policy rejection (size, links, type) concerns this entry only and keeps its named reason.
                                catch (InvalidDataException exception) { result = new(child, length, modified, null, Rejection: SourceEntryPolicy.ReasonOf(exception)); }
                            }
                        }
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                if (result is not null && !(result.Unreadable && IsIgnored(child, ignore))) yield return result;
            }
        }
    }
    private async ValueTask<IgnoreRule[]> LoadIgnoreAsync(string root, CancellationToken token)
    {
        StagedInput stage;
        try { stage = await stager.OpenSourceAsync(root, ".brainignore", token); }
        catch (IOException exception) when (exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }) { return []; }
        await using (stage)
        {
            if (stage.Length > 65536) throw new InvalidDataException("brainignore_bytes");
            if (extractor is null) throw new InvalidDataException("brainignore_extractor_required");
            var parsed = await extractor.ExtractAsync(stage.Content, ".txt", token);
            if (parsed.Status != "extracted" || parsed.PhysicalFormat != "text") throw new InvalidDataException("brainignore_invalid");
            var rules = parsed.LosslessText.Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0 && !line.StartsWith('#')).ToArray();
            if (rules.Length > 1000 || rules.Any(rule => rule.Length > 1024)) throw new InvalidDataException("brainignore_rules");
            return rules.Select(IgnoreRule.Parse).ToArray();
        }
    }
    private static bool IsIgnored(string path, IgnoreRule[] rules)
    {
        var ignored = false;
        foreach (var rule in rules)
            if (rule.Glob.IsMatch(path) || rule.MatchesName && rule.Glob.IsMatch(Path.GetFileName(path))) ignored = !rule.Include;
        return ignored;
    }
    private sealed record IgnoreRule(SourceGlob Glob, bool Include, bool MatchesName)
    {
        public static IgnoreRule Parse(string raw)
        {
            var include = raw.StartsWith('!'); var rule = include ? raw[1..] : raw;
            if (rule.StartsWith('/')) rule = rule[1..];
            if (rule.EndsWith('/')) rule += "**";
            return new(SourceGlob.Compile(rule), include, !rule.Contains('/'));
        }
    }
    private static SafeFileHandle Duplicate(SafeFileHandle file)
    {
        var fd = Fcntl(checked((int)file.DangerousGetHandle()), 1030, 0);
        if (fd < 0) throw new IOException("source_descriptor_duplicate"); return new(fd, true);
    }

    private static bool Matches(string path, SourceGlob[] include, SourceGlob[] exclude) =>
        (include.Length == 0 || include.Any(pattern => pattern.IsMatch(path))) && !exclude.Any(pattern => pattern.IsMatch(path));
    private static SafeFileHandle Open(string path)
    {
        var fd = OpenAt(-100, path, 0x10000 | 0x20000 | 0x80000); if (fd < 0) throw new IOException("source_open_failed"); return new(fd, true);
    }
    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string path, bool directory)
    {
        var parts = path.Split('/'); SafeFileHandle? current = null;
        try
        {
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i] is "" or "." or "..") throw new InvalidDataException("source_path");
                var fd = OpenAt(checked((int)(current ?? parent).DangerousGetHandle()), parts[i], 0x20000 | 0x80000 | 0x800 | (i < parts.Length - 1 || directory ? 0x10000 : 0));
                if (fd < 0) throw new IOException("source_open_failed", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError())); current?.Dispose(); current = new(fd, true);
            }
            var result = current!; current = null; return result;
        }
        finally { current?.Dispose(); }
    }
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int Fcntl(int descriptor, int command, int value);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)] private static extern int OpenAt(int directory, string path, int flags);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int Fstat(SafeFileHandle file, IntPtr stat);
}
