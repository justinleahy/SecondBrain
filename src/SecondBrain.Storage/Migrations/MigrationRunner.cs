using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Limits;
using SecondBrain.Core.Storage;

namespace SecondBrain.Storage.Migrations;

/// <summary>M0 migrations as separate durable state machines; no ATTACH or cross-store transaction is used.</summary>
public sealed class MigrationRunner : IMigrationRunner, IDisposable
{
    private const string MigrationStateDdl = """
        CREATE TABLE IF NOT EXISTS migration_state (
          version INTEGER PRIMARY KEY,
          phase TEXT NOT NULL CHECK (phase IN ('pending', 'applying', 'applied')),
          started_at TEXT,
          finished_at TEXT
        );
        """;

    private readonly IStateStore state;
    private readonly IIndexStore index;
    private readonly string dataRoot;
    private readonly IDiskCapacity diskCapacity;
    private readonly ICrashPoints crashPoints;
    private readonly MigrationOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>
    /// The caller holds the exclusive data-root lock and owns the injected stores. The disk probe must measure
    /// the filesystem actually mounted at the data root, which need not be the root filesystem.
    /// </summary>
    public MigrationRunner(IStateStore state, IIndexStore index, string dataRoot, IDiskCapacity diskCapacity, ICrashPoints? crashPoints = null, MigrationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(diskCapacity);
        this.state = state;
        this.index = index;
        this.dataRoot = Path.GetFullPath(dataRoot);
        this.diskCapacity = diskCapacity;
        this.crashPoints = crashPoints ?? new NoOpCrashPoints();
        this.options = options ?? new MigrationOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(this.options.MinimumFreeBytes);
    }

    /// <summary>Gets the immutable online-backup snapshot path for a state migration version.</summary>
    public string GetStateSnapshotPath(int version) => Path.Combine(dataRoot, "state", "migration-snapshots", $"pre-v{version.ToString(CultureInfo.InvariantCulture)}.db");

    /// <inheritdoc />
    public async ValueTask<MigrationReport> MigrateAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await NeedsMigrationAsync(state, cancellationToken).ConfigureAwait(false)
                || await NeedsMigrationAsync(index, cancellationToken).ConfigureAwait(false))
            {
                PreflightDisk();
            }
            // Pending markers for both files are durable before either schema is changed.
            await EnsurePendingAsync(state, cancellationToken).ConfigureAwait(false);
            await EnsurePendingAsync(index, cancellationToken).ConfigureAwait(false);
            var recoveries = new List<MigrationRecovery>();
            if (await MigrateStoreAsync(state, "state", recoveries, cancellationToken).ConfigureAwait(false))
            {
                await crashPoints.HitAsync(MigrationCrashPointNames.AfterStateMigration, cancellationToken).ConfigureAwait(false);
            }

            await MigrateStoreAsync(index, "index", recoveries, cancellationToken).ConfigureAwait(false);
            return new MigrationReport(M0Schema.Version, M0Schema.Version, recoveries.AsReadOnly());
        }
        finally
        {
            gate.Release();
        }
    }

    private static async ValueTask<bool> NeedsMigrationAsync(IStoreHandle store, CancellationToken cancellationToken)
    {
        await using var lease = await store.OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        var migrationTable = await MigrationSql.ScalarAsync(lease.Connection, null,
            "SELECT name FROM sqlite_schema WHERE type = 'table' AND name = 'migration_state';", cancellationToken).ConfigureAwait(false);
        if (migrationTable is null) return true;
        var phase = await MigrationSql.ScalarAsync(lease.Connection, null,
            "SELECT phase FROM migration_state WHERE version = $version;", cancellationToken, ("$version", M0Schema.Version)).ConfigureAwait(false);
        return !string.Equals(phase as string, "applied", StringComparison.Ordinal);
    }

    private void PreflightDisk()
    {
        long databaseBytes = 0;
        foreach (var path in new[] { Path.Combine(dataRoot, "state", "state.db"), Path.Combine(dataRoot, "index", "index.db") })
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(candidate))
                {
                    databaseBytes = checked(databaseBytes + new FileInfo(candidate).Length);
                }
            }
        }

        var required = checked(options.MinimumFreeBytes + databaseBytes * 3);
        // An unknown capacity fails closed; there is deliberately no fallback to the root filesystem.
        var available = diskCapacity.AvailableBytes(dataRoot)
            ?? throw new IOException("Migration disk preflight failed: available space on the data root's filesystem cannot be determined.");
        if (available < required)
        {
            throw new IOException($"Migration disk preflight failed: {required} bytes required; {available} bytes available.");
        }
    }

    private static async ValueTask EnsurePendingAsync(IStoreHandle store, CancellationToken cancellationToken)
    {
        await store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await MigrationSql.ExecuteAsync(connection, transaction, MigrationStateDdl, token).ConfigureAwait(false);
            if (await MigrationSql.ScalarAsync(connection, transaction,
                "SELECT version FROM migration_state WHERE version <> $version;", token, ("$version", M0Schema.Version)).ConfigureAwait(false) is { } unsupported)
            {
                throw new InvalidDataException($"Unsupported migration version {unsupported}; this binary supports M0 schema {M0Schema.Version}.");
            }

            await MigrationSql.ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO migration_state(version, phase) VALUES ($version, 'pending');", token,
                ("$version", M0Schema.Version)).ConfigureAwait(false);
            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> MigrateStoreAsync(IStoreHandle store, string name, List<MigrationRecovery> recoveries, CancellationToken cancellationToken)
    {
        var phase = await ReadPhaseAsync(store, cancellationToken).ConfigureAwait(false);
        if (phase == "applied")
        {
            await using var lease = await store.OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
            await ValidateSchemaAsync(lease.Connection, null, name, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var recovering = phase == "applying";
        var isState = name == "state";
        var snapshot = GetStateSnapshotPath(M0Schema.Version);
        if (isState)
        {
            if (recovering)
            {
                await VerifySnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await EnsureSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
        }

        await store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            return await MigrationSql.ExecuteAsync(connection, transaction,
                "UPDATE migration_state SET phase = 'applying', started_at = COALESCE(started_at, $at), finished_at = NULL WHERE version = $version;", token,
                ("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)), ("$version", M0Schema.Version)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        await crashPoints.HitAsync(isState ? MigrationCrashPointNames.AfterStateApplying : MigrationCrashPointNames.AfterIndexApplying, cancellationToken).ConfigureAwait(false);

        try
        {
            await ApplySchemaAsync(store, name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (recovering && isState && error is DbException or InvalidDataException)
        {
            // The first attempt rolled back. Restore only a snapshot verified again immediately before use.
            await VerifySnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            await RequireSnapshots().RestoreSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            await EnsurePendingAsync(state, cancellationToken).ConfigureAwait(false);
            await state.QueueWriteAsync(async (connection, transaction, token) =>
            {
                return await MigrationSql.ExecuteAsync(connection, transaction,
                    "UPDATE migration_state SET phase = 'applying', started_at = $at, finished_at = NULL WHERE version = $version;", token,
                    ("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)), ("$version", M0Schema.Version)).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            try
            {
                await ApplySchemaAsync(state, name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception retryError) when (retryError is DbException or InvalidDataException)
            {
                throw new InvalidDataException($"Interrupted state migration {M0Schema.Version} restored its verified pre-migration snapshot, but the retry failed; migration remains applying and startup must stop.", retryError);
            }
            recoveries.Add(new MigrationRecovery(name, M0Schema.Version, MigrationRecoveryMethod.RestoredSnapshot,
                $"Interrupted state migration {M0Schema.Version} could not be re-run ({error.GetType().Name}); restored its verified pre-migration snapshot and applied it successfully."));
            return true;
        }

        if (recovering)
        {
            recoveries.Add(new MigrationRecovery(name, M0Schema.Version, MigrationRecoveryMethod.ReranIdempotently,
                $"Interrupted {name} migration {M0Schema.Version} recovered by idempotent re-run."));
        }

        return true;
    }

    private static async ValueTask<string> ReadPhaseAsync(IStoreHandle store, CancellationToken cancellationToken)
    {
        await using var lease = await store.OpenReadConnectionAsync(cancellationToken).ConfigureAwait(false);
        var value = await MigrationSql.ScalarAsync(lease.Connection, null,
            "SELECT phase FROM migration_state WHERE version = $version;", cancellationToken, ("$version", M0Schema.Version)).ConfigureAwait(false);
        var phase = Convert.ToString(value, CultureInfo.InvariantCulture);
        return phase is "pending" or "applying" or "applied" ? phase : throw new InvalidDataException("Invalid migration phase.");
    }

    private static async ValueTask ApplySchemaAsync(IStoreHandle store, string name, CancellationToken cancellationToken)
    {
        await store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            await MigrationSql.ExecuteAsync(connection, transaction, name == "state" ? M0Schema.StateDdl : M0Schema.IndexDdl, token).ConfigureAwait(false);
            await ValidateSchemaAsync(connection, transaction, name, token).ConfigureAwait(false);
            var sql = name == "state"
                ? "INSERT INTO meta(key, value) VALUES ('schema_version', $version) ON CONFLICT(key) DO UPDATE SET value = excluded.value;"
                : "INSERT INTO index_meta(key, value) VALUES ('built_from_schema_version', $version) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            await MigrationSql.ExecuteAsync(connection, transaction, sql, token,
                ("$version", M0Schema.Version.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
            return await MigrationSql.ExecuteAsync(connection, transaction,
                "UPDATE migration_state SET phase = 'applied', finished_at = $at WHERE version = $version AND phase = 'applying';", token,
                ("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)), ("$version", M0Schema.Version)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ValidateSchemaAsync(DbConnection connection, DbTransaction? transaction, string name, CancellationToken cancellationToken)
    {
        foreach (var (table, expectedColumns) in name == "state" ? M0Schema.StateTables : M0Schema.IndexTables)
        {
            await using var command = MigrationSql.Command(connection, transaction, $"PRAGMA table_info({table});");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var actual = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                actual.Add(reader.GetString(1));
            }

            if (!expectedColumns.All(actual.Contains))
            {
                throw new InvalidDataException($"{name} schema table {table} is missing required Appendix A columns.");
            }
        }
    }

    private IStoreSnapshot RequireSnapshots() => state as IStoreSnapshot
        ?? throw new InvalidOperationException("The state store must implement IStoreSnapshot for migration online backup and recovery.");

    private async ValueTask EnsureSnapshotAsync(string snapshot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.GetDirectoryName(snapshot)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        MigrationFiles.SyncDirectory(Path.GetDirectoryName(Path.GetDirectoryName(snapshot)!)!);

        if (!File.Exists(snapshot))
        {
            await RequireSnapshots().CreateSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        // A crash between backup completion and sidecar creation may leave an intact snapshot without a digest.
        await CheckIntegrityAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var sidecar = snapshot + ".sha256";
        if (!File.Exists(sidecar))
        {
            var hash = await HashFileAsync(snapshot, cancellationToken).ConfigureAwait(false);
            var temporary = sidecar + ".tmp";
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temporary, fileOptions))
            {
                await file.WriteAsync(System.Text.Encoding.ASCII.GetBytes(hash), cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, sidecar, overwrite: false);
            MigrationFiles.SyncDirectory(Path.GetDirectoryName(snapshot)!);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(snapshot, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(sidecar, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await VerifySnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks both a snapshot's SHA-256 manifest and SQLite integrity before recovery can use it.</summary>
    public static async ValueTask VerifySnapshotAsync(string snapshot, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(snapshot) || !File.Exists(snapshot + ".sha256"))
        {
            throw new InvalidDataException("The pre-migration state snapshot or its SHA-256 manifest is missing; recovery stopped without restoring.");
        }

        var expected = (await File.ReadAllTextAsync(snapshot + ".sha256", cancellationToken).ConfigureAwait(false)).Trim();
        var actual = await HashFileAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The pre-migration state snapshot failed SHA-256 verification; recovery stopped without restoring.");
        }

        await CheckIntegrityAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask CheckIntegrityAsync(string snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = snapshot, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 30
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await MigrationSql.ExecuteAsync(connection, null, "PRAGMA trusted_schema = OFF;", cancellationToken).ConfigureAwait(false);
            await using var command = MigrationSql.Command(connection, null, "PRAGMA integrity_check;");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || !string.Equals(reader.GetString(0), "ok", StringComparison.Ordinal)
                || await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The pre-migration state snapshot failed SQLite integrity verification.");
            }
        }
        catch (SqliteException error)
        {
            throw new InvalidDataException("The pre-migration state snapshot is not a valid SQLite database.", error);
        }
    }

    /// <inheritdoc />
    public void Dispose() => gate.Dispose();
}
