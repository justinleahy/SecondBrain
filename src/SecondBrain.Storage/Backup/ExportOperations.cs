using Dapper;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Operations;
using SecondBrain.Storage.Durability;

namespace SecondBrain.Storage.Backup;

public sealed partial class BackupOperations
{
    public async Task<ExportDownload> ExportAsync(IngestionAuthority actor, CancellationToken token = default)
    {
        await AuthorizeAsync(actor, token);
        var gate = Gates.GetValue(state, static _ => new(1, 1));
        if (!await gate.WaitAsync(0, token)) throw new BackupException("backup_busy");
        try
        {
            var statePath = Path.Combine(Root, "state", "state.db");
            CheckCapacity(checked(new FileInfo(statePath).Length * 16));
            if (journal is not MutationJournal mutations) throw new InvalidOperationException("Export requires the durable mutation journal.");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var roots = await RootsAsync(state, token, includeKeyring: false);
                await using var captured = await BackupFilePins.CaptureAsync(mutations, snapshot, Root, roots, token);
                await using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = captured.StateSnapshotPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                {
                    await copy.OpenAsync(token);
                    var needed = await SourcePathsAsync(copy, token);
                    if (needed.Any(path => !roots.Any(root => root.Name.StartsWith("external", StringComparison.Ordinal) && Within(path, root.Path)))) continue;
                }
                long fileBytes = 0;
                await foreach (var file in captured.FilesAsync(token)) fileBytes = checked(fileBytes + file.Length);
                CheckCapacity(checked(new FileInfo(captured.StateSnapshotPath).Length * 16 + fileBytes * 3 + 1024 * 1024));
                await captured.CopyAllAsync(token);
                await AuthorizeAsync(actor, token);
                return await PortableExport.CreateAsync(captured, ct => AuthorizeAsync(actor, ct), token);
            }
            throw new BackupException("backup_sources_changed");
        }
        catch (OverflowException) { throw new BackupException("backup_capacity"); }
        finally { gate.Release(); }
    }
}
