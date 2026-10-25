using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Operations;

namespace SecondBrain.Storage.Backup;

/// <summary>Portable JSON Lines and original files, with an explicit exclusion policy for authentication data.</summary>
public static class PortableExport
{
    public static async Task<ExportDownload> CreateAsync(BackupFilePins snapshot,
        Func<CancellationToken, Task> authorize, CancellationToken token = default)
    {
        if (snapshot.Roots.Any(root => root.Name == "keyring")) throw new InvalidDataException("An ordinary export cannot contain the key ring.");
        await snapshot.CopyAllAsync(token);
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var owned = OperationWorkspace.Create(Path.Combine(snapshot.DataRoot, "staging"), "export");
        var workspace = owned.Path;
        try
        {
            var data = Path.Combine(workspace, "data");
            Directory.CreateDirectory(data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var state = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = snapshot.StateSnapshotPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await state.OpenAsync(token);
            await ValidateSchemaAsync(state, token);
            long rows = 0, files = 0, partial = 0;
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var table in ExportTable.Tables)
            {
                await authorize(token);
                var count = await WriteTableAsync(state, table, Path.Combine(data, table.Name + ".jsonl"), token);
                counts.Add(table.Name, count); rows += count;
            }
            await foreach (var file in snapshot.FilesAsync(token))
            { if (file.Status == "copied") files++; else partial++; }
            var metadata = Path.Combine(workspace, "export.json");
            await using (var output = PrivateFile(metadata))
                await JsonSerializer.SerializeAsync(output, new
                {
                    format = "secondbrain-export", version = 1, captured_at = snapshot.CapturedAt,
                    instance_id = await state.ExecuteScalarAsync<string>(new CommandDefinition("SELECT value FROM meta WHERE key='instance_id'", cancellationToken: token)),
                    time_zone = await state.ExecuteScalarAsync<string>(new CommandDefinition("SELECT value FROM meta WHERE key='time_zone'", cancellationToken: token)),
                    files, partial_files = partial, rows, tables = counts,
                    columns = ExportTable.Tables.ToDictionary(table => table.Name, table => table.IncludedColumns.Split(',')),
                    roots = snapshot.Roots.Select(root => new { name = root.Name, source_path = root.Path }),
                    encoding = "UTF-8 JSON Lines; BLOB values are objects with a $base64 string",
                    file_paths = "Files use per-export numeric directories and safe names; manifest source_root and source_path preserve original locations",
                    omitted = new[] { "authentication records", "key ring", "protected calendar links and HTTP validators", "idempotency cache", "disposable index", "server configuration and secrets" }
                }, cancellationToken: token);
            var archive = Path.Combine(workspace, "export.tar");
            var manifestPath = Path.Combine(workspace, "manifest.jsonl");
            await using (var manifest = PrivateFile(manifestPath))
            await using (var output = PrivateFile(archive))
            {
                await using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
                {
                    await AddFileAsync(writer, manifest, "export.json", metadata, snapshot.CapturedAt, token);
                    foreach (var table in ExportTable.Tables)
                        await AddFileAsync(writer, manifest, "data/" + table.Name + ".jsonl", Path.Combine(data, table.Name + ".jsonl"), snapshot.CapturedAt, token);
                    await foreach (var file in snapshot.FilesAsync(token))
                    {
                        var name = "files/" + file.Root + "/" + file.Id.ToString("D12", System.Globalization.CultureInfo.InvariantCulture) + "/" + SafeName(file.RelativePath);
                        if (file.Status == "copied")
                            await AddFileAsync(writer, manifest, name, file.CopyPath!, snapshot.CapturedAt, token, file.Sha256, file.Root, file.RelativePath);
                        else
                            await WriteLineAsync(manifest, new { path = name, source_root = file.Root, source_path = file.RelativePath, status = "partial", error = file.Error }, token);
                    }
                    await manifest.FlushAsync(token);
                    await using var manifestRead = File.OpenRead(manifestPath);
                    await writer.WriteEntryAsync(Entry("manifest.jsonl", manifestRead, snapshot.CapturedAt), token);
                }
                output.Flush(true);
            }
            await authorize(token);
            Directory.Delete(data, true); File.Delete(metadata); File.Delete(manifestPath);
            var archiveName = "secondbrain-export-" + snapshot.CapturedAt.UtcDateTime.ToString("yyyyMMddTHHmmssfffffffZ", System.Globalization.CultureInfo.InvariantCulture)
                + (partial == 0 ? ".tar" : ".partial.tar");
            return new(archiveName, new ExportStream(archive, owned), rows, files, partial);
        }
        catch { owned.Dispose(); throw; }
    }

    private static async Task ValidateSchemaAsync(SqliteConnection state, CancellationToken token)
    {
        var tables = (await state.QueryAsync<string>(new CommandDefinition("SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%'", cancellationToken: token))).ToHashSet(StringComparer.Ordinal);
        if (tables.Except(ExportTable.Tables.Select(table => table.Name).Concat(ExportTable.OmittedTables), StringComparer.Ordinal).Any())
            throw new InvalidDataException("The state schema requires an updated export policy.");
        foreach (var table in ExportTable.Tables)
        {
            var columns = (await state.QueryAsync<Column>(new CommandDefinition("PRAGMA table_info(" + table.Name + ")", cancellationToken: token))).Select(column => column.Name);
            if (!columns.SequenceEqual(table.ExpectedColumns.Split(','))) throw new InvalidDataException("The state schema requires an updated export policy.");
        }
    }
    private static async Task<long> WriteTableAsync(SqliteConnection state, ExportTable table, string path, CancellationToken token)
    {
        await using var output = PrivateFile(path);
        await using var command = state.CreateCommand();
        command.CommandText = "SELECT " + table.IncludedColumns + " FROM " + table.Name + " ORDER BY rowid";
        await using var reader = await command.ExecuteReaderAsync(token);
        long rows = 0;
        while (await reader.ReadAsync(token))
        {
            using (var json = new Utf8JsonWriter(output))
            {
                json.WriteStartObject();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    json.WritePropertyName(reader.GetName(i));
                    switch (reader.GetValue(i))
                    {
                        case DBNull: json.WriteNullValue(); break;
                        case long value: json.WriteNumberValue(value); break;
                        case double value: json.WriteNumberValue(value); break;
                        case string value:
                            if (value.Length > 64 * 1024 * 1024) throw new InvalidDataException("An export field exceeds its size limit.");
                            json.WriteStringValue(value); break;
                        case byte[] value:
                            if (value.Length > 64 * 1024 * 1024) throw new InvalidDataException("An export field exceeds its size limit.");
                            json.WriteStartObject(); json.WriteBase64String("$base64", value); json.WriteEndObject(); break;
                        default: throw new InvalidDataException("An export field has an unsupported storage type.");
                    }
                }
                json.WriteEndObject(); await json.FlushAsync(token);
            }
            await output.WriteAsync("\n"u8.ToArray(), token); rows++;
        }
        output.Flush(true); return rows;
    }
    private static async Task AddFileAsync(TarWriter writer, Stream manifest, string name, string path,
        DateTimeOffset captured, CancellationToken token, string? expectedHash = null, string? sourceRoot = null, string? sourcePath = null)
    {
        await using var input = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
        if (expectedHash is not null && hash != expectedHash) throw new InvalidDataException("A pinned export file changed.");
        input.Position = 0;
        await writer.WriteEntryAsync(Entry(name, input, captured), token);
        await WriteLineAsync(manifest, new { path = name, source_root = sourceRoot, source_path = sourcePath, status = "complete", bytes = input.Length, sha256 = hash }, token);
    }
    private static string SafeName(string path)
    {
        var original = Path.GetFileName(path);
        var escaped = Uri.EscapeDataString(original);
        var suffix = "";
        while (escaped.EndsWith(".", StringComparison.Ordinal)) { escaped = escaped[..^1]; suffix += "%2E"; }
        escaped = "f-" + escaped + suffix;
        return escaped.Length <= 200 ? escaped : "h-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original)));
    }
    private static PaxTarEntry Entry(string name, Stream input, DateTimeOffset captured) => new(TarEntryType.RegularFile, name)
    { DataStream = input, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite, Uid = 0, Gid = 0, UserName = "", GroupName = "", ModificationTime = captured };
    private static FileStream PrivateFile(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new(path, new FileStreamOptions
        { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read, Options = FileOptions.Asynchronous, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
    }
    private static async Task WriteLineAsync<T>(Stream stream, T value, CancellationToken token)
    { await JsonSerializer.SerializeAsync(stream, value, cancellationToken: token); await stream.WriteAsync("\n"u8.ToArray(), token); }
    private sealed class Column { public string Name { get; set; } = ""; }
    private sealed class ExportStream(string path, OperationWorkspace workspace) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
    {
        private int cleaned;
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Interlocked.Exchange(ref cleaned, 1) == 0) workspace.Dispose();
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Interlocked.Exchange(ref cleaned, 1) == 0) workspace.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
