using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;

namespace SecondBrain.Storage.Backup;

public sealed record RestoreRoot(string Name, string Original, string Destination);
public sealed record RestorePreparationResult(long AccountEpoch, long RemappedValues);

/// <summary>Prepares an isolated, verified state copy; never opens the live database for writing.</summary>
public static class RestorePreparation
{
    public static IReadOnlyList<RestoreRoot> MapRoots(BackupPackageMetadata metadata, string dataRoot,
        IReadOnlyDictionary<string, string> externalMappings, IReadOnlyList<string> allowedExternalRoots)
    {
        dataRoot = Absolute(dataRoot);
        var original = Absolute(metadata.DataRoot);
        if (dataRoot == Path.GetPathRoot(dataRoot) || original == Path.GetPathRoot(original)) throw Invalid();
        foreach (var name in new[] { "state", "vault", "trash", "keyring" })
            if (metadata.Roots.Count(root => root.Name == name && root.Kind == name) != 1) throw Invalid();
        var external = metadata.Roots.Where(root => root.Kind == "external").ToArray();
        if (externalMappings.Keys.Any(key => !external.Any(root => root.SourcePath == key))) throw Invalid();
        var result = metadata.Roots.Select(root => root.Kind == "external"
            ? new RestoreRoot(root.Name, Absolute(root.SourcePath!), Absolute(externalMappings.GetValueOrDefault(root.SourcePath!, root.SourcePath!)))
            : new RestoreRoot(root.Name, Path.Combine(original, root.Name), Path.Combine(dataRoot, root.Name))).ToList();
        foreach (var root in result.Where(root => root.Name.StartsWith("external", StringComparison.Ordinal)))
        {
            if (Within(root.Original, original) || Within(original, root.Original)
                || Within(root.Destination, dataRoot) || Within(dataRoot, root.Destination)
                || !allowedExternalRoots.Any(allowed => Within(root.Destination, Absolute(allowed)))) throw Invalid();
        }
        for (var i = 0; i < result.Count; i++)
            for (var j = i + 1; j < result.Count; j++)
                if (Within(result[i].Destination, result[j].Destination) || Within(result[j].Destination, result[i].Destination)
                    || Within(result[i].Original, result[j].Original) || Within(result[j].Original, result[i].Original)) throw Invalid();
        return result;
    }

    public static async Task<RestorePreparationResult> PrepareStateAsync(string stagedStatePath,
        BackupPackageMetadata metadata, IReadOnlyList<RestoreRoot> roots, string targetDataRoot, long existingEpoch,
        CancellationToken token = default)
    {
        var mappings = new[] { new RestoreRoot("data", Absolute(metadata.DataRoot), Absolute(targetDataRoot)) }
            .Concat(roots.Where(root => root.Name.StartsWith("external", StringComparison.Ordinal))).OrderByDescending(root => root.Original.Length).ToArray();
        var epoch = checked(Math.Max(existingEpoch, metadata.AccountEpoch) + 1);
        await using var state = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = stagedStatePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await state.OpenAsync(token);
        await state.ExecuteAsync(new CommandDefinition("PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA temp_store=FILE; PRAGMA temp.cache_size=-2048", cancellationToken: token));
        var needsUpgrade = await Migrations.M4SchemaTransition.NeedsAsync(state, token);
        await using var transaction = state.BeginTransaction();
        if (needsUpgrade)
        {
            await Migrations.M4SchemaTransition.PrepareAsync(state, transaction, null, token);
            await Migrations.M4SchemaTransition.ApplyStateAsync(state, transaction, token);
        }
        if (await state.ExecuteScalarAsync<long>(Sql("SELECT COUNT(*) FROM mutations WHERE status IN ('prepared','applied')", null, transaction, token)) != 0)
            throw new InvalidDataException("A backup with unfinished file mutations cannot be installed.");
        long changed = 0;
        foreach (var (table, column) in new[] { ("occurrences", "path"), ("absences", "path"), ("mutations", "destination"),
            ("pending_operations", "destination"), ("calendar_subscriptions", "snapshot_path"), ("calendar_subscription_publications", "snapshot_path") })
            changed += await RewriteColumnAsync(state, transaction, table, column, value => Remap(value, mappings), token);
        changed += await RewriteColumnAsync(state, transaction, "sources", "config_json", value => RewriteJson(value, node => MapProperty(node, "path", mappings)), token);
        changed += await RewriteColumnAsync(state, transaction, "jobs", "payload_json", value => RewriteJson(value, node =>
        {
            foreach (var name in new[] { "Path", "ClaimRoot", "ClaimPath", "path", "claim_root", "claim_path" }) MapProperty(node, name, mappings);
        }), token);

        // Only internal projection and journal metadata is rewritten. Document text, arbitrary properties,
        // protected envelopes, original file bytes and recorded assistant content remain byte-for-byte intact.
        // Select row ids once. Repeating the payload-id subquery under OR makes SQLite rebuild
        // and sort the full projection set for every row at large corpus sizes.
        await state.ExecuteAsync(Sql("""
            CREATE TEMP TABLE restore_payload_rows(row_id INTEGER PRIMARY KEY);
            INSERT OR IGNORE INTO restore_payload_rows
              SELECT p.rowid FROM document_projections d JOIN payloads p ON p.id=d.payload_id WHERE p.redacted_at IS NULL;
            INSERT OR IGNORE INTO restore_payload_rows
              SELECT rowid FROM payloads WHERE redacted_at IS NULL AND
                (id GLOB 'sb_mutation_*_plan' OR id GLOB 'sb_mutation_*_conflict' OR id GLOB 'sb_mutation_*_external-projection');
            """, null, transaction, token));
        long after = 0;
        while (true)
        {
            var rows = (await state.QueryAsync<Payload>(Sql("""
                SELECT p.rowid RowId,p.id Id,p.bytes Bytes
                FROM restore_payload_rows selected CROSS JOIN payloads p
                WHERE selected.row_id>@after AND p.rowid=selected.row_id
                ORDER BY selected.row_id LIMIT 1
                """, new { after }, transaction, token))).ToArray();
            if (rows.Length == 0) break;
            foreach (var row in rows)
            {
                after = row.RowId;
                var node = JsonNode.Parse(row.Bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 64 })?.AsObject() ?? throw Invalid();
                var before = node.ToJsonString();
                MapProperty(node, "Path", mappings); MapProperty(node, "ConflictPath", mappings); MapProperty(node, "SourcePath", mappings);
                if (node["RemovalTargets"] is JsonArray removals)
                    foreach (var removal in removals.OfType<JsonObject>()) MapProperty(removal, "Path", mappings);
                if (node.ToJsonString() == before) continue;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(node);
                await state.ExecuteAsync(Sql("UPDATE payloads SET bytes=@bytes,sha256=@hash WHERE rowid=@rowId",
                    new { bytes, hash = Convert.ToHexStringLower(SHA256.HashData(bytes)), rowId = row.RowId }, transaction, token));
                changed++;
            }
        }
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await state.ExecuteAsync(Sql("""
            UPDATE meta SET value=@epoch WHERE key='account_epoch';
            INSERT INTO meta(key,value) VALUES('restored_at',@now) ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            UPDATE credentials SET revoked_at=COALESCE(revoked_at,@now),stepped_up_at=NULL;
            UPDATE pending_operations SET status='expired',resolved_at=@now WHERE status IN ('pending','approved','executing');
            UPDATE jobs SET status='cancelled',lease_until=NULL,error='restore_requires_retry',updated_at=@now WHERE status IN ('queued','running');
            UPDATE calendar_subscriptions SET lease_id=NULL,lease_until=NULL;
            DELETE FROM folder_scan_candidates;
            DELETE FROM idempotency;
            """, new { epoch = epoch.ToString(CultureInfo.InvariantCulture), now }, transaction, token));
        transaction.Commit();
        if (await state.ExecuteScalarAsync<string>(new CommandDefinition("PRAGMA integrity_check", cancellationToken: token)) != "ok") throw Invalid();
        await using (var violations = await state.ExecuteReaderAsync(new CommandDefinition("PRAGMA foreign_key_check", cancellationToken: token)))
            if (await violations.ReadAsync(token)) throw Invalid();
        await state.CloseAsync();
        using var durable = new FileStream(stagedStatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        durable.Flush(true);
        return new(epoch, changed);
    }

    private static async Task<long> RewriteColumnAsync(SqliteConnection state, SqliteTransaction transaction,
        string table, string column, Func<string, string> rewrite, CancellationToken token)
    {
        long after = 0, changed = 0;
        while (true)
        {
            var rows = (await state.QueryAsync<ValueRow>(Sql($"SELECT rowid RowId,{column} Value FROM {table} WHERE rowid>@after AND {column} IS NOT NULL ORDER BY rowid LIMIT 128", new { after }, transaction, token))).ToArray();
            if (rows.Length == 0) return changed;
            foreach (var row in rows)
            {
                after = row.RowId; var value = rewrite(row.Value);
                if (value == row.Value) continue;
                await using var update = state.CreateCommand(); update.Transaction = transaction;
                update.CommandText = $"UPDATE {table} SET {column}=@value WHERE rowid=@rowId";
                update.Parameters.AddWithValue("@value", value); update.Parameters.AddWithValue("@rowId", row.RowId);
                await update.ExecuteNonQueryAsync(token); changed++;
            }
        }
    }
    private static string RewriteJson(string value, Action<JsonObject> rewrite)
    {
        var node = JsonNode.Parse(value)?.AsObject() ?? throw Invalid();
        var before = node.ToJsonString(); rewrite(node);
        return before == node.ToJsonString() ? value : node.ToJsonString();
    }
    private static void MapProperty(JsonObject node, string name, IReadOnlyList<RestoreRoot> mappings)
    {
        if (node[name] is not JsonValue value || !value.TryGetValue<string>(out var path)) return;
        var remapped = Remap(path, mappings);
        if (remapped != path) node[name] = remapped;
    }
    private static string Remap(string path, IReadOnlyList<RestoreRoot> mappings)
    {
        if (!Path.IsPathFullyQualified(path)) return path;
        foreach (var mapping in mappings)
            if (Within(path, mapping.Original)) return Path.GetFullPath(Path.Combine(mapping.Destination, Path.GetRelativePath(mapping.Original, path)));
        return path;
    }
    internal static string Absolute(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0')) throw Invalid();
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    internal static bool Within(string path, string root) => path == root || path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private static InvalidDataException Invalid() => new("The restore roots or state metadata are invalid.");
    private static CommandDefinition Sql(string sql, object? args, SqliteTransaction transaction, CancellationToken token) => new(sql, args, transaction, cancellationToken: token);
    private sealed class ValueRow { public long RowId { get; init; } public string Value { get; init; } = ""; }
    private sealed class Payload { public long RowId { get; init; } public string Id { get; init; } = ""; public byte[] Bytes { get; init; } = []; }
}
