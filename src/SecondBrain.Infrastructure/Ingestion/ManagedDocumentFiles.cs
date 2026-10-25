using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Infrastructure.Ingestion;

/// <summary>Preserves captured original bytes through the shared crash-safe, conflict-preserving journal.</summary>
public sealed class ManagedDocumentFiles(string dataRoot, IMutationJournal journal) : IManagedDocumentFiles, IDisposable
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IMutationJournal, SemaphoreSlim> CalendarFileGates = new();
    private readonly SemaphoreSlim calendarFiles = CalendarFileGates.GetValue(journal, static _ => new(1, 1));
    private readonly InputStager stager = new(Path.Combine(dataRoot, "staging"));
    public ValueTask<int> CleanupCalendarSnapshotsAsync(CancellationToken cancellationToken = default) =>
        journal.CleanupUnpublishedCalendarSnapshotsAsync(cancellationToken: cancellationToken);

    public ValueTask<int> PurgeOwnedCalendarFilesAsync(string containerId, string operationId, CancellationToken cancellationToken = default) =>
        PurgeOwnedDocumentFilesAsync(containerId, operationId, cancellationToken);
    public async ValueTask<int> PurgeOwnedDocumentFilesAsync(string containerId, string operationId, CancellationToken cancellationToken = default)
    {
        _ = GetManagedPath(containerId, ".ics");
        if (operationId.Length is < 1 or > 128 || operationId.Any(character => !char.IsAsciiLetterOrDigit(character))) throw new ArgumentException("Invalid purge operation.");
        await calendarFiles.WaitAsync(cancellationToken);
        try
        {
            await journal.RecoverDocumentAsync(containerId, cancellationToken);
            await journal.RecoverCalendarPurgeAsync(operationId, cancellationToken);
            await journal.RecoverCalendarSnapshotsAsync(containerId, cancellationToken);
            var marker = Path.Combine(Path.GetFullPath(dataRoot), "vault", "tombstones", "source-" + operationId + ".purge");
            InputStager.EnsurePrivateDirectory(Path.GetDirectoryName(marker)!);
            var total = 0;
            while (true)
            {
                var removals = new List<MutationRemovalTarget>(); var metadataBytes = 0;
                foreach (var category in new[] { "vault", "trash" })
                {
                    var root = Path.Combine(Path.GetFullPath(dataRoot), category);
                    if (!Directory.Exists(root)) continue;
                    var scanner = new FolderSourceFiles(stager, purgeScan: true);
                    var configuration = new SecondBrain.Core.Sources.FolderSourceConfiguration(root, ["**/" + containerId + ".*"], [], true, "index", false, null, new Dictionary<string, string>(), "poll", 20);
                    await foreach (var entry in scanner.ScanAsync(configuration, deep: true, cancellationToken))
                    {
                        if (entry.Unreadable || entry.Rejection is not null) throw new InvalidDataException("purge_owned_file_unreadable");
                        var path = Path.Combine(root, entry.RelativePath);
                        var cost = System.Text.Encoding.UTF8.GetByteCount(path) + 128;
                        if (removals.Count > 0 && metadataBytes + cost > 32 * 1024) break;
                        removals.Add(new(path, entry.Sha256!)); metadataBytes += cost;
                        if (removals.Count >= 128) break;
                    }
                    if (removals.Count > 0) break;
                }
                if (removals.Count == 0) return total;
                var mutation = "calendar-purge-files:" + Guid.NewGuid().ToString("N");
                var result = await journal.ExecuteAsync(new MutationWriteRequest
                {
                    MutationId = mutation, OperationId = operationId, Kind = "purge", Destination = marker, Content = [],
                    ExpectedHash = File.Exists(marker) ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([])) : null,
                    RemovalTargets = removals, DocumentReferences = [containerId]
                }, cancellationToken);
                if (result.Status == "conflict") throw new ManagedFileConflictException(result.Id, result.ConflictPath);
                if (result.Status != "finalized") throw new IOException("calendar_purge_not_finalized");
                total += removals.Count;
            }
        }
        finally { calendarFiles.Release(); }
    }
    public async ValueTask<string> WriteCalendarSnapshotAsync(string containerId, string snapshotId, byte[] bytes, CancellationToken cancellationToken = default)
    {
        await calendarFiles.WaitAsync(cancellationToken);
        try { return await WriteCalendarSnapshotCoreAsync(containerId, snapshotId, bytes, cancellationToken); }
        finally { calendarFiles.Release(); }
    }
    private async ValueTask<string> WriteCalendarSnapshotCoreAsync(string containerId, string snapshotId, byte[] bytes, CancellationToken cancellationToken)
    {
        var original = GetManagedPath(containerId, ".ics");
        if (snapshotId.Length is < 1 or > 128 || snapshotId.Any(character => !char.IsAsciiLetterOrDigit(character))) throw new ArgumentException("Invalid snapshot identifier.");
        if (bytes.LongLength > InputStager.MaximumInputBytes) throw new InvalidDataException("input_bytes");
        var path = Path.Combine(Path.GetDirectoryName(original)!, containerId + "." + snapshotId + ".ics");
        InputStager.EnsurePrivateDirectory(Path.GetDirectoryName(path)!);
        var mutation = "calendar-snapshot:" + snapshotId;
        var result = await journal.ExecuteAsync(new MutationWriteRequest
        {
            MutationId = mutation, OperationId = mutation, Kind = "import", Destination = path,
            Content = bytes, DocumentReferences = [containerId]
        }, cancellationToken);
        if (result.Status == "conflict") throw new ManagedFileConflictException(result.Id, result.ConflictPath);
        if (result.Status != "finalized") throw new IOException("calendar_snapshot_not_finalized");
        return path;
    }
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

    public string GetAssistantNotePath(string documentId)
    {
        var original = GetManagedPath(documentId, ".md");
        return Path.Combine(Path.GetFullPath(dataRoot), "vault", "notes", Path.GetFileName(original));
    }

    public async ValueTask<string> ApplyLifecycleAsync(string documentId, string? managedPath, string extension, string operation, string? expectedHash, byte[] fallbackContent, MutationFinalization finalization, CancellationToken cancellationToken = default)
    {
        await calendarFiles.WaitAsync(cancellationToken);
        try { return await ApplyLifecycleCoreAsync(documentId, managedPath, extension, operation, expectedHash, fallbackContent, finalization, cancellationToken); }
        finally { calendarFiles.Release(); }
    }
    private async ValueTask<string> ApplyLifecycleCoreAsync(string documentId, string? managedPath, string extension, string operation, string? expectedHash, byte[] fallbackContent, MutationFinalization finalization, CancellationToken cancellationToken)
    {
        var target = GetLifecyclePath(documentId, extension, operation);
        var removals = new List<MutationRemovalTarget>(); byte[] bytes = fallbackContent;
        if (managedPath is not null)
        {
            try
            {
                await using var source = await OpenReadAsync(managedPath, cancellationToken);
                using var copied = new MemoryStream(); await source.CopyToAsync(copied, cancellationToken); bytes = copied.ToArray();
            }
            catch (IOException error) when (operation == "purge" && expectedHash is not null &&
                (error is FileNotFoundException || error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }))
            {
                // The journal still checks this expected hash if the file reappears before removal.
                bytes = [];
            }
            removals.Add(new(Path.GetFullPath(managedPath), expectedHash ?? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))));
        }
        if (operation == "purge")
        {
            bytes = [];
            foreach (var category in new[] { "vault", "trash" })
            {
                var root = Path.Combine(Path.GetFullPath(dataRoot), category);
                if (!Directory.Exists(root)) continue;
                var scanner = new FolderSourceFiles(stager, purgeScan: true);
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
        var mutation = finalization.MutationIdentity ?? Guid.NewGuid().ToString("N");
        var result = await journal.ExecuteAsync(new MutationWriteRequest
        {
            MutationId = mutation, OperationId = mutation, Kind = operation == "delete" ? "trash" : operation,
            Destination = target, Content = bytes, RemovalTargets = removals, DocumentReferences = [documentId], Finalization = finalization
        }, cancellationToken);
        if (result.Status == "conflict") throw new ManagedFileConflictException(result.Id, result.ConflictPath);
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
        var destination = finalization.AssistantNote ? GetAssistantNotePath(documentId) : GetManagedPath(documentId, extension);
        InputStager.EnsurePrivateDirectory(Path.Combine(Path.GetFullPath(dataRoot), "vault"));
        InputStager.EnsurePrivateDirectory(Path.GetDirectoryName(destination)!);
        var mutation = finalization.MutationIdentity ?? Guid.NewGuid().ToString("N");
        var result = await journal.ExecuteAsync(new MutationWriteRequest
        {
            MutationId = mutation, OperationId = mutation, Kind = finalization.EnrichmentWriteback is null ? "import" : "write_back", Destination = destination,
            Content = bytes, ExpectedHash = expectedHash, DocumentReferences = [documentId], Finalization = finalization
        }, cancellationToken);
        if (result.Status == "conflict") throw new ManagedFileConflictException(result.Id, result.ConflictPath);
        if (result.Status != "finalized") throw new IOException("managed_capture_not_finalized");
        return destination;
    }
}
