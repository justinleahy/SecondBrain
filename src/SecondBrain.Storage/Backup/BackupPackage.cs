using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace SecondBrain.Storage.Backup;

public sealed record BackupPackageRoot(string Name, string Kind, string? SourcePath = null);
public sealed record BackupPackageMetadata(int Version, DateTimeOffset CapturedAt, string DataRoot, string InstanceId, long AccountEpoch,
    int StateSchemaVersion, string StateSchemaSha256, int IndexSchemaVersion, IReadOnlyList<JsonElement> ProcessingGenerations,
    IReadOnlyList<JsonElement> EmbeddingSpaces, IReadOnlyList<BackupPackageRoot> Roots)
{
    public string Kind => "metadata";
}
public sealed record BackupPackageEntry(string Root, string Path, long Length, string Status, string? Sha256 = null,
    string? Error = null, string? DocumentId = null, long? Revision = null)
{
    public string Kind => "file";
}
public sealed record BackupPackageSummary(long Files, long Complete, long Partial, long Bytes, string ManifestSha256)
{
    public string Kind => "end";
}
public sealed record BackupRestoreLimits(long MaximumPlaintextBytes = 64L * 1024 * 1024 * 1024, long MaximumFiles = 1_000_000);

/// <summary>Bounded, versioned manifest records and verified file bodies inside BackupCipher's authenticated stream.</summary>
public static partial class BackupPackage
{
    private static ReadOnlySpan<byte> Magic => "SBKP0001"u8;
    private const int MaximumMetadataBytes = 1024 * 1024;
    private const int MaximumRecordBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<BackupPackageMetadata> DescribeAsync(BackupFilePins snapshot, int indexSchemaVersion, CancellationToken token = default)
    {
        await using var state = OpenState(snapshot.StateSnapshotPath); await state.OpenAsync(token);
        var rows = (await state.QueryAsync<string>(Sql("""
            SELECT json_object('id',id,'fingerprint',fingerprint,'provider',provider,'model',model,'model_revision',model_revision,
              'dimensions',dimensions,'chunk_generation',chunk_generation,'status',status,'created_at',created_at,'activated_at',activated_at,'retired_at',retired_at)
            FROM embedding_spaces ORDER BY id LIMIT 129
            """, null, token))).ToArray();
        if (rows.Length > 128) throw new InvalidDataException("Backup embedding metadata exceeds its bound.");
        return await DescribeAsync(snapshot,indexSchemaVersion,rows.Select(row=>JsonSerializer.Deserialize<JsonElement>(row)).ToArray(),token);
    }

    /// <summary>Embedding descriptors and index version must have been captured with the same version set.</summary>
    public static async Task<BackupPackageMetadata> DescribeAsync(BackupFilePins snapshot, int indexSchemaVersion,
        IReadOnlyList<JsonElement> embeddingSpaces, CancellationToken token = default)
    {
        await using var state = OpenState(snapshot.StateSnapshotPath); await state.OpenAsync(token);
        var instance = await state.ExecuteScalarAsync<string?>(Sql("SELECT value FROM meta WHERE key='instance_id'", null, token))
            ?? throw new InvalidDataException("The backup snapshot has no instance identity.");
        var epoch = await state.ExecuteScalarAsync<long>(Sql("SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch'", null, token));
        var schema = await SchemaVersionAsync(state, token);
        var schemaHash = await SchemaHashAsync(state, token);
        var generations = (await state.QueryAsync<string>(Sql("SELECT DISTINCT generation_json FROM publication_fences ORDER BY generation_json LIMIT 129", null, token))).ToArray();
        if (generations.Length > 128) throw new InvalidDataException("Backup generation metadata exceeds its bound.");
        var roots = snapshot.Roots.Select(root => new BackupPackageRoot(root.Name,
            root.Name is "vault" or "trash" or "keyring" ? root.Name : "external",
            root.Name is "vault" or "trash" or "keyring" ? null : root.Path)).Prepend(new("state", "state")).ToArray();
        var metadata = new BackupPackageMetadata(1, snapshot.CapturedAt, snapshot.DataRoot, instance, epoch, schema, schemaHash, indexSchemaVersion,
            generations.Select(value => JsonSerializer.Deserialize<JsonElement>(value)).ToArray(), embeddingSpaces, roots);
        ValidateMetadata(metadata); return metadata;
    }

    public static async Task<BackupPackageSummary> WriteAsync(BackupFilePins snapshot, Stream archive,
        ReadOnlyMemory<byte> backupKey, BackupPackageMetadata metadata, CancellationToken token = default)
    {
        ValidateMetadata(metadata);
        if (!metadata.Roots.Where(root => root.Kind != "state").Select(root => root.Name).Order().SequenceEqual(snapshot.Roots.Select(root => root.Name).Order()))
            throw InvalidPackage();
        if (metadata.DataRoot != snapshot.DataRoot || metadata.CapturedAt != snapshot.CapturedAt || snapshot.Roots.Any(root =>
            metadata.Roots.Single(item => item.Name == root.Name) is var described && described.Kind == "external" && described.SourcePath != root.Path))
            throw InvalidPackage();
        await snapshot.CopyAllAsync(token);
        await using var state = OpenState(snapshot.StateSnapshotPath); await state.OpenAsync(token);
        await VerifyStateAsync(state, metadata, token);
        await using var revisions = await CreateRevisionLookupAsync(state, snapshot.WorkspacePath, token);
        await using var writer = await BackupCipher.OpenEncryptionAsync(archive, backupKey, token);
        await writer.WriteAsync(Magic.ToArray(), token);
        using var manifest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await WriteRecordAsync(writer, metadata, MaximumMetadataBytes, manifest, token);
        long count = 0, complete = 0, partial = 0, bytes = 0;
        var stateInfo = new FileInfo(snapshot.StateSnapshotPath);
        string stateHash;
        await using (var source = File.OpenRead(snapshot.StateSnapshotPath)) stateHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, token));
        await WriteFileAsync(new("state", "state.db", stateInfo.Length, "complete", stateHash), snapshot.StateSnapshotPath);
        await foreach (var row in snapshot.FilesAsync(token))
        {
            if (row.Status is not ("copied" or "partial")) throw InvalidPackage();
            var sourceRoot = snapshot.Roots.Single(root => root.Name == row.Root);
            var sourcePath = Path.GetFullPath(Path.Combine(sourceRoot.Path, row.RelativePath));
            var revision = row.Status == "copied" ? await FindRevisionAsync(state, revisions, sourcePath, row.Sha256!, token) : null;
            await WriteFileAsync(new(row.Root, row.RelativePath, row.Length, row.Status == "copied" ? "complete" : "partial",
                row.Sha256, row.Error, revision?.Id, revision?.Revision), row.CopyPath);
        }
        var summary = new BackupPackageSummary(count, complete, partial, bytes, Convert.ToHexStringLower(manifest.GetHashAndReset()));
        await WriteRecordAsync(writer, summary, MaximumRecordBytes, null, token);
        await writer.CompleteAsync(token);
        return summary;

        async Task WriteFileAsync(BackupPackageEntry entry, string? path)
        {
            ValidateEntry(entry, metadata);
            await WriteRecordAsync(writer, entry, MaximumRecordBytes, manifest, token);
            count = checked(count + 1);
            if (entry.Status == "partial") { partial++; return; }
            if (path is null) throw InvalidPackage();
            await using var file = File.OpenRead(path);
            await CopyVerifiedAsync(file, writer, entry.Length, entry.Sha256!, token);
            complete++; bytes = checked(bytes + entry.Length);
        }
    }

    private static async Task WriteRecordAsync<T>(Stream destination, T value, int bound, IncrementalHash? manifest, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (body.Length > bound) throw InvalidPackage();
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, body.Length);
        manifest?.AppendData(prefix); manifest?.AppendData(body);
        await destination.WriteAsync(prefix, token); await destination.WriteAsync(body, token);
    }
    private static async Task<byte[]> ReadRecordAsync(Stream source, int bound, IncrementalHash? manifest, CancellationToken token)
    {
        var prefix = new byte[4]; await source.ReadExactlyAsync(prefix, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is < 2 || length > bound) throw InvalidPackage();
        var body = new byte[length]; await source.ReadExactlyAsync(body, token);
        manifest?.AppendData(prefix); manifest?.AppendData(body); return body;
    }
    private static async Task CopyVerifiedAsync(Stream source, Stream destination, long length, string expectedHash, CancellationToken token, bool requireEnd = true)
    {
        var buffer = new byte[65536]; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), token);
                if (read == 0) throw InvalidPackage();
                remaining -= read; hash.AppendData(buffer, 0, read); await destination.WriteAsync(buffer.AsMemory(0, read), token);
            }
            if (requireEnd && source.CanSeek && source.Position != source.Length || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(expectedHash)))
                throw InvalidPackage();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
    private static void ValidateMetadata(BackupPackageMetadata value)
    {
        if (value.Version != 1 || value.DataRoot is null || !Path.IsPathFullyQualified(value.DataRoot) || value.DataRoot.Length > 4096 || value.DataRoot.Contains('\0') || value.CapturedAt == default || string.IsNullOrWhiteSpace(value.InstanceId) || value.InstanceId.Length > 128 || value.AccountEpoch < 0 || value.StateSchemaVersion < 1 || value.StateSchemaSha256 is not { Length: 64 } || !value.StateSchemaSha256.All(char.IsAsciiHexDigit) || value.IndexSchemaVersion < 0
            || value.ProcessingGenerations is null || value.EmbeddingSpaces is null || value.Roots is null || value.Roots.Count is < 1 or > 129
            || value.ProcessingGenerations.Count > 128 || value.EmbeddingSpaces.Count > 128) throw InvalidPackage();
        if (value.ProcessingGenerations.Concat(value.EmbeddingSpaces).Any(item => item.ValueKind != JsonValueKind.Object || item.GetRawText().Length > 4096)) throw InvalidPackage();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in value.Roots)
        {
            if (root is null) throw InvalidPackage();
            ValidateRootName(root.Name);
            if (!names.Add(root.Name) || root.Kind is not ("state" or "vault" or "trash" or "keyring" or "external")
                || root.Kind != "external" && root.Name != root.Kind
                || root.Kind == "external" && root.Name != "external" && !root.Name.StartsWith("external/", StringComparison.Ordinal)
                || root.Kind == "external" && (root.SourcePath is null || !Path.IsPathFullyQualified(root.SourcePath) || root.SourcePath.Length > 4096)
                || root.Kind != "external" && root.SourcePath is not null) throw InvalidPackage();
        }
        if (value.Roots.Count(root => root.Kind == "state") != 1 || names.Any(name => names.Any(other => name != other && name.StartsWith(other + '/', StringComparison.Ordinal)))) throw InvalidPackage();
    }
    private static void ValidateEntry(BackupPackageEntry entry, BackupPackageMetadata metadata)
    {
        if (!metadata.Roots.Any(root => root.Name == entry.Root) || entry.Length < 0 || entry.Status is not ("complete" or "partial")) throw InvalidPackage();
        ValidatePath(entry.Path, entry.Status == "partial");
        if (entry.Status == "complete" && (entry.Sha256 is not { Length: 64 } || !entry.Sha256.All(char.IsAsciiHexDigit) || entry.Error is not null)
            || entry.Status == "partial" && (entry.Sha256 is not null || string.IsNullOrEmpty(entry.Error) || entry.Error.Length > 64 || entry.Error.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')))
            throw InvalidPackage();
        if (entry.DocumentId is not null && (entry.DocumentId.Length is < 1 or > 128 || entry.DocumentId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) || entry.Revision is <= 0 || entry.Revision is not null && entry.DocumentId is null) throw InvalidPackage();
        if (entry.Root == "state" && (entry.Path != "state.db" || entry.Status != "complete")) throw InvalidPackage();
    }
    private static void ValidateRootName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128 || name.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))) throw InvalidPackage();
    }
    private static void ValidatePath(string path, bool allowRoot)
    {
        if (allowRoot && path == ".") return;
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || path.Contains('\0') || Path.IsPathRooted(path) || path.Split('/').Any(part => part.Length == 0 || part is "." or "..")) throw InvalidPackage();
    }
    private static SqliteConnection OpenState(string path) => new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    private static async Task VerifyStateAsync(SqliteConnection state, BackupPackageMetadata metadata, CancellationToken token)
    {
        if (await state.ExecuteScalarAsync<string>(Sql("PRAGMA integrity_check", null, token)) != "ok"
            || await SchemaVersionAsync(state, token) != metadata.StateSchemaVersion
            || await SchemaHashAsync(state, token) != metadata.StateSchemaSha256
            || await state.ExecuteScalarAsync<string?>(Sql("SELECT value FROM meta WHERE key='instance_id'", null, token)) != metadata.InstanceId
            || await state.ExecuteScalarAsync<long>(Sql("SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch'", null, token)) != metadata.AccountEpoch) throw InvalidPackage();
        await using var foreignKeys = state.CreateCommand(); foreignKeys.CommandText = "PRAGMA foreign_key_check";
        await using var violations = await foreignKeys.ExecuteReaderAsync(token);
        if (await violations.ReadAsync(token)) throw InvalidPackage();
    }
    internal static async Task<int> SchemaVersionAsync(System.Data.Common.DbConnection database, CancellationToken token)
    {
        var version = await database.ExecuteScalarAsync<int>(Sql("SELECT COALESCE(MAX(version),0) FROM migration_state WHERE phase='applied'", null, token));
        if (await database.ExecuteScalarAsync<int>(Sql("SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='app_migrations'", null, token)) != 0)
            version = Math.Max(version, await database.ExecuteScalarAsync<int>(Sql("SELECT COALESCE(MAX(version),0) FROM app_migrations WHERE phase='applied'", null, token)));
        return version;
    }
    private static async Task<string> SchemaHashAsync(SqliteConnection state, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var command = state.CreateCommand();
        command.CommandText = "SELECT type,name,tbl_name,COALESCE(sql,'') FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name";
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new[] { reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3) }));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static async Task<RevisionInfo?> FindRevisionAsync(SqliteConnection state, SqliteConnection revisions, string path, string hash, CancellationToken token)
    {
        var exact = await revisions.QueryFirstOrDefaultAsync<RevisionInfo>(Sql(
            "SELECT id Id,revision Revision FROM projected_files WHERE path=@path AND hash=@hash ORDER BY id LIMIT 1",
            new { path, hash }, token));
        if (exact is not null) return exact;
        var id = Path.GetFileName(path).Split('.')[0];
        return await state.QueryFirstOrDefaultAsync<RevisionInfo>(Sql("SELECT document_id Id,revision Revision FROM revisions WHERE document_id=@id AND content_hash=@hash ORDER BY revision DESC LIMIT 1", new { id, hash }, token));
    }
    private static CommandDefinition Sql(string text, object? arguments, CancellationToken token) => new(text, arguments, cancellationToken: token);
    private static InvalidDataException InvalidPackage() => new("The backup package or its manifest is invalid.");
    private sealed class RevisionInfo { public string Id { get; set; } = ""; public long Revision { get; set; } }
}
