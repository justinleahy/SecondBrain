using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Sources;

namespace SecondBrain.Infrastructure.FileSystem;

/// <summary>Resolves every path segment, including symlinked parents, before applying FLD-1.</summary>
/// <remarks>
/// Protected trees are the fixed system locations, the data root, and the configuration and secrets locations
/// this process actually loaded (they may be relocated anywhere, including beneath an allowed root).
/// </remarks>
public sealed class SourcePathValidator(IOptionsMonitor<SecondBrainOptions> options, RuntimeLocations locations) : ISourcePathValidator
{
    private static readonly string[] ProtectedRoots = ["/proc", "/sys", "/dev", "/etc/secondbrain", "/run/secrets", "/var/log", "/private/var/log", "/Library/Logs"];

    public SourcePathValidation Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !Path.IsPathFullyQualified(path))
        {
            return Deny("The source path must be an absolute directory path.");
        }

        try
        {
            var config = options.CurrentValue;
            var lexical = Path.GetFullPath(path);
            var denied = ProtectedRoots.Append(Path.GetFullPath(config.DataRoot)).Concat(RuntimeProtectedRoots()).ToArray();
            if (denied.Any(root => OverlapsProtectedTree(lexical, root)))
            {
                return Deny("The source path is within a protected directory.");
            }

            var canonical = ResolveDirectory(path);
            // The data root may itself have symlinked ancestors; reject its physical tree too.
            var physicalDenied = denied.Select(ResolveProtectedDirectory).ToArray();
            if (physicalDenied.Any(root => OverlapsProtectedTree(canonical, root)))
            {
                return Deny("The source path resolves within a protected directory.");
            }

            var allowed = config.Sources.AllowedRoots
                .Where(root => Path.IsPathFullyQualified(root) && Directory.Exists(root))
                .Select(root => ResolveDirectory(root))
                .Where(root => !physicalDenied.Any(protectedRoot => AtOrBelow(root, protectedRoot)))
                .Any(root => AtOrBelow(canonical, root));
            return allowed ? new(true, canonical, null) : Deny("The source path resolves outside the configured allowed roots.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Deny("The source path cannot be resolved to an existing accessible directory.");
        }
    }

    private static SourcePathValidation Deny(string reason) => new(false, null, reason);

    private List<string> RuntimeProtectedRoots()
    {
        // Each entry is also resolved physically by the caller, covering aliases of its ancestors.
        List<string> roots = [locations.SecretsDirectory, locations.ConfigDirectory];
        // A symlinked configuration file is read through every hop, so each hop's directory is a config directory too.
        var current = locations.ConfigPath;
        for (var links = 0; ; links++)
        {
            string? next;
            try { next = new FileInfo(current).LinkTarget; }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { break; }
            if (next is null) break;
            if (links == 40) throw new IOException("Too many symbolic links.");
            // The kernel follows a relative target from the link's physical directory and resolves a link/.. in the
            // target from that link's destination, so neither may be collapsed lexically.
            var target = Path.IsPathFullyQualified(next) ? next : Path.Combine(ResolveDirectory(Path.GetDirectoryName(current)!), next);
            string directory;
            try { directory = ResolveDirectory(Path.GetDirectoryName(target)!); }
            catch (DirectoryNotFoundException)
            {
                // A dangling hop cannot be read through; protect its lexical location and stop.
                roots.Add(Path.GetFullPath(Path.GetDirectoryName(target)!));
                break;
            }

            roots.Add(directory);
            current = Path.Combine(directory, Path.GetFileName(target));
        }

        return roots;
    }

    private static bool OverlapsProtectedTree(string source, string protectedRoot) => AtOrBelow(source, protectedRoot) || AtOrBelow(protectedRoot, source);

    private static string ResolveProtectedDirectory(string path)
    {
        // A protected directory may not have been provisioned yet. Resolve its existing
        // ancestors so aliases such as /etc -> /private/etc cannot expose its future tree.
        var suffix = new Stack<string>();
        while (!Directory.Exists(path))
        {
            suffix.Push(Path.GetFileName(path));
            path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException();
        }

        var resolved = ResolveDirectory(path);
        foreach (var segment in suffix)
        {
            resolved = Path.Combine(resolved, segment);
        }

        return resolved;
    }

    internal static bool AtOrBelow(string candidate, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.Equals(root, comparison) || candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison);
    }

    private static string ResolveDirectory(string path) => ResolveDirectory(path, 0);

    private static string ResolveDirectory(string path, int links)
    {
        if (links > 40)
        {
            throw new IOException("Too many symbolic links.");
        }

        var root = Path.GetPathRoot(path) ?? throw new IOException("Missing path root.");
        var current = root;
        // Do not collapse '..' before resolving links: a link/.. refers to the link target's parent.
        foreach (var segment in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                current = Directory.GetParent(current)?.FullName ?? root;
                continue;
            }

            var directory = new DirectoryInfo(Path.Combine(current, segment));
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException();
            }

            if (OperatingSystem.IsMacOS())
            {
                // macOS supports both case-sensitive and case-insensitive volumes. Use the
                // actual directory entry's spelling, then keep ordinal boundary comparisons.
                // An exact match wins on a case-sensitive volume with names differing by case.
                string? foldedMatch = null;
                string? exactMatch = null;
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    var name = Path.GetFileName(entry);
                    if (name.Equals(segment, StringComparison.Ordinal))
                    {
                        exactMatch = entry;
                        break;
                    }

                    if (name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                    {
                        foldedMatch = entry;
                    }
                }

                directory = new DirectoryInfo(exactMatch ?? foldedMatch ?? throw new DirectoryNotFoundException());
            }

            if (directory.LinkTarget is { } target)
            {
                current = ResolveDirectory(Path.IsPathFullyQualified(target) ? target : Path.Combine(current, target), links + 1);
            }
            else
            {
                current = directory.FullName;
            }
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }
}
