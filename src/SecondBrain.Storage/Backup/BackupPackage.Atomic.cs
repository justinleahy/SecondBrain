using SecondBrain.Storage.Connections;
using SecondBrain.Storage.Migrations;

namespace SecondBrain.Storage.Backup;

public sealed record BackupArchive(string FileName, string Path, long Length, BackupPackageSummary Summary);

public static partial class BackupPackage
{
    public static async Task<BackupArchive> WriteAtomicAsync(BackupFilePins snapshot, string directory,
        ReadOnlyMemory<byte> backupKey, BackupPackageMetadata metadata, CancellationToken token = default, Func<CancellationToken, Task>? authorizePublication = null)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null)
            throw new ArgumentException("Backup output requires an existing absolute directory without links.", nameof(directory));
        var id = Guid.NewGuid().ToString("N"); var temporary = Path.Combine(directory, ".backup-" + id + ".tmp");
        try
        {
            BackupPackageSummary summary;
            await using (var stream = OpenPrivateFile(temporary, FileMode.CreateNew))
            {
                summary = await WriteAsync(snapshot, stream, backupKey, metadata, token);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            if (authorizePublication is not null) await authorizePublication(token);
            var name = "backup-" + metadata.CapturedAt.UtcDateTime.ToString("yyyyMMddTHHmmssfffffffZ", System.Globalization.CultureInfo.InvariantCulture)
                + "-" + id + (summary.Partial == 0 ? ".sbk" : ".partial.sbk");
            var destination = Path.Combine(directory, name);
            File.Move(temporary, destination, overwrite: false);
            ConnectionFactory.MakePrivate(destination); MigrationFiles.SyncDirectory(directory);
            return new(name, destination, new FileInfo(destination).Length, summary);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
