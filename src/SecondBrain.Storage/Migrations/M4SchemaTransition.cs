using System.Data.Common;
using Dapper;

namespace SecondBrain.Storage.Migrations;

internal static class M4SchemaTransition
{
    internal const int Version = 8;
    internal const string LedgerDdl = """
        CREATE TABLE IF NOT EXISTS app_migrations(
          version INTEGER PRIMARY KEY, phase TEXT NOT NULL CHECK(phase IN ('applying','applied')),
          backup_name TEXT, started_at TEXT NOT NULL, finished_at TEXT
        );
        """;
    internal static async Task<bool> NeedsAsync(DbConnection connection, CancellationToken token)
    {
        if (await connection.ExecuteScalarAsync<int>(Sql("SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='app_migrations'", null, null, token)) == 0) return true;
        if (await connection.ExecuteScalarAsync<int>(Sql("SELECT COUNT(*) FROM app_migrations WHERE version>@version", new { version = Version }, null, token)) != 0)
            throw new InvalidDataException("The database uses a newer application schema.");
        return await connection.ExecuteScalarAsync<int>(Sql("SELECT COUNT(*) FROM app_migrations WHERE version=@version AND phase='applied'", new { version = Version }, null, token)) == 0;
    }
    internal static async Task PrepareAsync(DbConnection connection, DbTransaction transaction, string? backupName, CancellationToken token)
    {
        await connection.ExecuteAsync(Sql(LedgerDdl, null, transaction, token));
        await connection.ExecuteAsync(Sql("""
            INSERT INTO app_migrations(version,phase,backup_name,started_at) VALUES(@version,'applying',@backup,@now)
            ON CONFLICT(version) DO UPDATE SET phase='applying',backup_name=COALESCE(excluded.backup_name,backup_name),finished_at=NULL
            """, new { version = Version, backup = backupName, now = DateTimeOffset.UtcNow.ToString("O") }, transaction, token));
    }
    internal static async Task ApplyStateAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var columns = (await connection.QueryAsync<Column>(Sql("PRAGMA table_info(documents)", null, transaction, token))).ToArray();
        if (!columns.Any(column => column.Name == "container_document_id"))
            await connection.ExecuteAsync(Sql("""
                ALTER TABLE documents ADD COLUMN container_document_id TEXT REFERENCES documents(id);
                DROP INDEX IF EXISTS documents_natural_key;
                CREATE UNIQUE INDEX documents_natural_key ON documents(source_id,natural_key)
                  WHERE natural_key IS NOT NULL AND container_document_id IS NULL;
                """, null, transaction, token));
        await connection.ExecuteAsync(Sql(M4Schema.StateDdl, null, transaction, token));
        await connection.ExecuteAsync(Sql(UsageSchema.Ddl, null, transaction, token));
        await connection.ExecuteAsync(Sql(EnrichmentSchema.StateDdl, null, transaction, token));
        var batchColumns = (await connection.QueryAsync<Column>(Sql("PRAGMA table_info(batch_jobs)", null, transaction, token))).ToArray();
        if (!batchColumns.Any(column => column.Name == "metadata_json"))
            await connection.ExecuteAsync(Sql("ALTER TABLE batch_jobs ADD COLUMN metadata_json TEXT NOT NULL DEFAULT '{}';", null, transaction, token));
        var occurrenceColumns = (await connection.QueryAsync<Column>(Sql("PRAGMA table_info(occurrences)", null, transaction, token))).ToArray();
        if (!occurrenceColumns.Any(column => column.Name == "source_content_hash"))
        {
            await connection.ExecuteAsync(Sql("ALTER TABLE occurrences ADD COLUMN source_content_hash TEXT;", null, transaction, token));
            await connection.ExecuteAsync(Sql("""
                UPDATE occurrences SET source_content_hash=COALESCE(
                  (SELECT r.content_hash FROM revisions r WHERE r.document_id=occurrences.document_id AND r.cause IN ('source-admit','source-edit') ORDER BY r.revision DESC LIMIT 1),
                  (SELECT d.content_hash FROM documents d WHERE d.id=occurrences.document_id));
                """, null, transaction, token));
        }
        await connection.ExecuteAsync(Sql("UPDATE app_migrations SET phase='applied',finished_at=@now WHERE version=@version",
            new { now = DateTimeOffset.UtcNow.ToString("O"), version = Version }, transaction, token));
    }
    internal static async Task ApplyIndexAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        await connection.ExecuteAsync(Sql(M4Schema.IndexDdl + EnrichmentSchema.IndexDdl + LedgerDdl, null, transaction, token));
        await connection.ExecuteAsync(Sql("UPDATE index_meta SET value=@version WHERE key='built_from_schema_version'",
            new { version = Version.ToString(System.Globalization.CultureInfo.InvariantCulture) }, transaction, token));
        await connection.ExecuteAsync(Sql("""
            INSERT INTO app_migrations(version,phase,started_at,finished_at) VALUES(@version,'applied',@now,@now)
            ON CONFLICT(version) DO UPDATE SET phase='applied',finished_at=excluded.finished_at
            """, new { version = Version, now = DateTimeOffset.UtcNow.ToString("O") }, transaction, token));
    }
    private static CommandDefinition Sql(string sql, object? args, DbTransaction? transaction, CancellationToken token) => new(sql, args, transaction, cancellationToken: token);
    private sealed class Column { public string Name { get; init; } = ""; }
}
