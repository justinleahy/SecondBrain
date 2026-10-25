using System.Globalization;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Operations;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Migrations;

namespace SecondBrain.Storage.Backup;

public sealed partial class BackupOperations
{
    public async Task<BackupRetentionResult> RotateAsync(IngestionAuthority? actor = null, CancellationToken token = default)
    {
        if (actor is not null) await AuthorizeAsync(actor, token);
        var gate = Gates.GetValue(state, static _ => new(1, 1));
        if (!await gate.WaitAsync(0, token)) return new(0, 0, true, 1);
        try { return await RotateCoreAsync(null, actor, token); }
        finally { gate.Release(); }
    }

    private async Task<BackupRetentionResult> RotateCoreAsync(string? preserve, IngestionAuthority? actor, CancellationToken token)
    {
        if (!Directory.Exists(ArchiveDirectory)) return new(0, 0, false);
        RequireDirectory(ArchiveDirectory);
        var deferred = 0;
        // A crash may leave an archive quarantined before unlink. Restore its name before recomputing policy.
        foreach (var path in Directory.EnumerateFiles(ArchiveDirectory, ".pruning-*"))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path)[9..];
            if (!ArchiveName().IsMatch(name)) continue;
            if (!IsArchive(path)) { deferred++; continue; }
            if (actor is not null) await AuthorizeAsync(actor, token);
            try { File.Move(path, Path.Combine(ArchiveDirectory, name), false); MigrationFiles.SyncDirectory(ArchiveDirectory); }
            catch (IOException) { deferred++; }
        }
        var keep = options.CurrentValue.Backup.Keep;
        var newest = new SortedSet<string>(StringComparer.Ordinal);
        string? complete = null;
        foreach (var path in Directory.EnumerateFiles(ArchiveDirectory))
        {
            token.ThrowIfCancellationRequested(); var name = Path.GetFileName(path);
            if (!ValidName(name) || !IsArchive(path)) continue;
            newest.Add(name); if (newest.Count > keep) newest.Remove(newest.Min!);
            if (!name.EndsWith(".partial.sbk", StringComparison.Ordinal) && (complete is null || StringComparer.Ordinal.Compare(name, complete) > 0)) complete = name;
        }
        if (complete is not null) newest.Add(complete);
        if (preserve is not null) newest.Add(preserve);
        var deleted = 0; var more = deferred != 0;
        foreach (var path in Directory.EnumerateFiles(ArchiveDirectory))
        {
            token.ThrowIfCancellationRequested(); var name = Path.GetFileName(path);
            if (!ValidName(name) || newest.Contains(name) || !IsArchive(path)) continue;
            if (deleted >= 64) { more = true; break; }
            if (actor is not null) await AuthorizeAsync(actor, token);
            try
            {
                using var parent = new ManagedMutationFiles(Root).Open(path, "backup-rotation");
                using var before = parent.OpenSnapshotRead();
                var quarantined = Path.Combine(ArchiveDirectory, ".pruning-" + name);
                File.Move(path, quarantined, false); MigrationFiles.SyncDirectory(ArchiveDirectory);
                using var displaced = new ManagedMutationFiles(Root).Open(quarantined, "backup-rotation-check");
                using var check = displaced.OpenSnapshotRead();
                if (check.Identity.FileId != before.Identity.FileId || check.Identity.Length != before.Identity.Length)
                {
                    File.Move(quarantined, path, false); deferred++; more = true; continue;
                }
                File.Delete(quarantined); MigrationFiles.SyncDirectory(ArchiveDirectory); deleted++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { deferred++; more = true; }
        }
        return new(deleted, newest.Count, more, deferred);
    }

    private static bool ValidName(string name) => ArchiveName().IsMatch(name)
        && DateTimeOffset.TryParseExact(name.Substring(7, 23), "yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);

    private bool IsArchive(string path)
    {
        try
        {
            using var parent = new ManagedMutationFiles(Root).Open(path, "backup-retention-read");
            using var input = parent.OpenSnapshotRead();
            Span<byte> header = stackalloc byte[8]; input.Stream.ReadExactly(header);
            return header.SequenceEqual("SBKB0001"u8);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }
}
