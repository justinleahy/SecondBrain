using Microsoft.Data.Sqlite;

namespace SecondBrain.Storage.Backup;

public static partial class BackupPackage
{
    private static async Task<SqliteConnection> CreateRevisionLookupAsync(SqliteConnection state, string workspace, CancellationToken token)
    {
        var path = Path.Combine(workspace, "revisions-" + Guid.NewGuid().ToString("N") + ".db");
        using (OpenPrivateFile(path, FileMode.CreateNew)) { }
        var lookup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            await lookup.OpenAsync(token);
            await using (var schema = lookup.CreateCommand())
            {
                // The private workspace is discarded after interruption. Keep its cache
                // bounded and reserve durability for the published encrypted archive.
                schema.CommandText = """
                    PRAGMA journal_mode=DELETE; PRAGMA synchronous=OFF; PRAGMA cache_size=-2048;
                    CREATE TABLE projected_files(path TEXT NOT NULL,hash TEXT NOT NULL,id TEXT NOT NULL,revision INTEGER NOT NULL,
                      PRIMARY KEY(path,hash,id)) WITHOUT ROWID;
                    """;
                await schema.ExecuteNonQueryAsync(token);
            }
            await using var transaction = lookup.BeginTransaction();
            await using var insert = lookup.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO projected_files(path,hash,id,revision) VALUES($path,$hash,$id,$revision)";
            var pathParameter = insert.Parameters.Add("$path", SqliteType.Text);
            var hashParameter = insert.Parameters.Add("$hash", SqliteType.Text);
            var idParameter = insert.Parameters.Add("$id", SqliteType.Text);
            var revisionParameter = insert.Parameters.Add("$revision", SqliteType.Integer);
            await using var select = state.CreateCommand();
            select.CommandText = """
                SELECT json_extract(CAST(b.bytes AS TEXT),'$.Path'),d.content_hash,d.id,d.revision
                FROM documents d JOIN document_projections p ON p.document_id=d.id JOIN payloads b ON b.id=p.payload_id
                WHERE b.redacted_at IS NULL
                """;
            await using var reader = await select.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (reader.GetValue(0) is not string sourcePath) continue;
                pathParameter.Value = sourcePath;
                hashParameter.Value = reader.GetString(1);
                idParameter.Value = reader.GetString(2);
                revisionParameter.Value = reader.GetInt64(3);
                await insert.ExecuteNonQueryAsync(token);
            }
            await transaction.CommitAsync(token);
            return lookup;
        }
        catch
        {
            await lookup.DisposeAsync();
            throw;
        }
    }
}
