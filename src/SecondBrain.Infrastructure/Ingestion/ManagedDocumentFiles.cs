using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Infrastructure.Ingestion;

/// <summary>Preserves captured original bytes through the shared crash-safe, conflict-preserving journal.</summary>
public sealed class ManagedDocumentFiles(string dataRoot, IMutationJournal journal) : IManagedDocumentFiles, IDisposable
{
    private readonly InputStager stager = new(Path.Combine(dataRoot, "staging"));
    public async ValueTask<Stream> OpenReadAsync(string managedPath, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(managedPath);
        var root = new[] { "vault", "trash" }.Select(name => Path.Combine(Path.GetFullPath(dataRoot), name)).FirstOrDefault(candidate => path.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.Ordinal)) ?? throw new InvalidDataException("managed_path");
        var stage = await stager.OpenSourceAsync(root, Path.GetRelativePath(root, path), cancellationToken);
        return stage.Content; // Caller owns the sole unlinked, read-only descriptor.
    }
    public void Dispose() => stager.Dispose();

    public string GetManagedPath(string documentId, string extension)
    {
        if (documentId.Length is < 1 or > 128 || documentId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')) throw new ArgumentException("Invalid document identifier.", nameof(documentId));
        if (extension.Length is < 2 or > 16 || extension[0] != '.' || extension[1..].Any(character => !char.IsAsciiLetterOrDigit(character))) throw new ArgumentException("Invalid extension.", nameof(extension));
        return Path.Combine(Path.GetFullPath(dataRoot), "vault", "originals", documentId + extension.ToLowerInvariant());
    }

    public string GetLifecyclePath(string documentId, string extension, string operation)
    {
        var original = GetManagedPath(documentId, extension);
        return operation switch
        {
            "delete" => Path.Combine(Path.GetFullPath(dataRoot), "trash", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), "originals", Path.GetFileName(original)),
            "restore" => original,
            "purge" => Path.Combine(Path.GetFullPath(dataRoot), "vault", "tombstones", documentId + ".tombstone"),
            _ => throw new ArgumentException("Invalid lifecycle operation.", nameof(operation))
        };
    }

    public async ValueTask<string> ApplyLifecycleAsync(string documentId, string? managedPath, string extension, string operation, string? expectedHash, byte[] fallbackContent, MutationFinalization finalization, CancellationToken cancellationToken = default)
    {
        var target = GetLifecyclePath(documentId, extension, operation);
        var removals = new List<MutationRemovalTarget>(); byte[] bytes = fallbackContent;
        if (managedPath is not null)
        {
            await using var source = await OpenReadAsync(managedPath, cancellationToken);
            using var copied = new MemoryStream(); await source.CopyToAsync(copied, cancellationToken); bytes = copied.ToArray();
            removals.Add(new(Path.GetFullPath(managedPath), expectedHash ?? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))));
        }
        if (operation == "purge")
        {
            bytes = [];
            foreach (var category in new[] { "vault", "trash" })
            {
                var root = Path.Combine(Path.GetFullPath(dataRoot), category);
                if (!Directory.Exists(root)) continue;
                var scanner = new FolderSourceFiles(stager);
                var configuration = new SecondBrain.Core.Sources.FolderSourceConfiguration(root, ["**/" + documentId + ".*"], [], true, "index", false, null, new Dictionary<string,string>(), "poll", 20);
                await foreach (var entry in scanner.ScanAsync(configuration, deep: true, cancellationToken))
                {
                    // An owned copy that cannot be staged (unreadable, linked, oversized) must fail the purge, never survive it.
                    var unstaged = entry.Unreadable || entry.Rejection is not null;
                    if (unstaged && Path.GetFileName(entry.RelativePath).StartsWith(documentId + ".", StringComparison.Ordinal)) throw new InvalidDataException("purge_owned_file_unreadable");
                    if (unstaged) continue;
                    var path = Path.Combine(root, entry.RelativePath);
                    if (path == target || removals.Any(removal => removal.Path == path)) continue;
                    if (removals.Count >= 1000) throw new InvalidDataException("purge_owned_files");
                    removals.Add(new(path, entry.Sha256!));
                }
            }
        }
        if (removals.Any(removal => removal.Path == target)) throw new InvalidDataException("lifecycle_already_at_destination");
        InputStager.EnsurePrivateDirectory(Path.GetDirectoryName(target)!);
        var mutation = Guid.NewGuid().ToString("N");
        var result = await journal.ExecuteAsync(new MutationWriteRequest
        {
            MutationId = mutation, OperationId = mutation, Kind = operation == "delete" ? "trash" : operation,
            Destination = target, Content = bytes, RemovalTargets = removals, DocumentReferences = [documentId], Finalization = finalization
        }, cancellationToken);
        if (result.Status == "conflict") throw new IOException("managed_lifecycle_conflict");
        if (result.Status != "finalized") throw new IOException("managed_lifecycle_not_finalized");
        return target;
    }

    public async ValueTask DiscardUnpublishedAsync(string managedPath, CancellationToken cancellationToken = default) =>
        await journal.DiscardUnpublishedImportAsync(Path.GetFullPath(managedPath), cancellationToken);

    public ValueTask<string> WriteAsync(string documentId, byte[] bytes, string extension, string? expectedHash = null, CancellationToken cancellationToken = default) => WriteRevisionAsync(documentId, bytes, extension, expectedHash, new(), cancellationToken);

    public async ValueTask<string> WriteRevisionAsync(string documentId, byte[] bytes, string extension, string? expectedHash, MutationFinalization finalization, CancellationToken cancellationToken = default)
    {
        if (documentId.Length is < 1 or > 128 || documentId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')) throw new ArgumentException("Invalid document identifier.", nameof(documentId));
        if (extension.Length is < 1 or > 16 || extension[0] != '.' || extension[1..].Any(character => !char.IsAsciiLetterOrDigit(character))) throw new ArgumentException("Invalid extension.", nameof(extension));
        if (bytes.LongLength > InputStager.MaximumInputBytes) throw new InvalidDataException("input_bytes");
        var destination = GetManagedPath(documentId, extension);
        InputStager.EnsurePrivateDirectory(Path.Combine(Path.GetFullPath(dataRoot), "vault"));
        InputStager.EnsurePrivateDirectory(Path.GetDirectoryName(destination)!);
        var mutation = Guid.NewGuid().ToString("N");
        var result = await journal.ExecuteAsync(new MutationWriteRequest
        {
            MutationId = mutation, OperationId = mutation, Kind = "import", Destination = destination,
            Content = bytes, ExpectedHash = expectedHash, DocumentReferences = [documentId], Finalization = finalization
        }, cancellationToken);
        if (result.Status == "conflict") throw new IOException("managed_capture_conflict");
        if (result.Status != "finalized") throw new IOException("managed_capture_not_finalized");
        return destination;
    }
}
