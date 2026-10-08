using System.Globalization;
using System.Text.Json;
using Dapper;
using SecondBrain.Core.Sources;
using SecondBrain.Core.Storage;

namespace SecondBrain.Storage.Sources;

/// <summary>Uses lane A's queued writer and Appendix A column names without owning connections.</summary>
public sealed class SqliteSourceRepository(IStateStore store) : ISourceRepository
{
    public async Task AddAsync(SourceRecord source, CancellationToken cancellationToken = default)
    {
        await store.QueueWriteAsync(async (connection, transaction, token) =>
        {
            return await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO sources (id, kind, name, config_json, status, last_scan_at, last_full_scan_at, created_at)
                VALUES (@Id, @Kind, @Name, @ConfigJson, @Status, @LastScanAt, @LastFullScanAt, @CreatedAt)
                """,
                new
                {
                    source.Id,
                    source.Kind,
                    source.Name,
                    ConfigJson = JsonSerializer.Serialize(source.Config),
                    source.Status,
                    LastScanAt = source.LastScanAt?.ToString("O", CultureInfo.InvariantCulture),
                    LastFullScanAt = source.LastFullScanAt?.ToString("O", CultureInfo.InvariantCulture),
                    CreatedAt = source.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                }, transaction, cancellationToken: token));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<SourceRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await store.OpenReadConnectionAsync(cancellationToken);
        var rows = await lease.Connection.QueryAsync<SourceRow>(new CommandDefinition(
            """
            SELECT id AS Id, kind AS Kind, name AS Name, config_json AS ConfigJson, status AS Status,
                   last_scan_at AS LastScanAt, last_full_scan_at AS LastFullScanAt, created_at AS CreatedAt
            FROM sources WHERE kind = 'folder' ORDER BY created_at, id
            """, cancellationToken: cancellationToken));
        return rows.Select(row => new SourceRecord(
            row.Id, row.Kind, row.Name,
            JsonSerializer.Deserialize<FolderSourceConfiguration>(row.ConfigJson) ?? throw new InvalidDataException("Invalid source configuration."),
            row.Status, ParseTimestamp(row.LastScanAt), ParseTimestamp(row.LastFullScanAt),
            DateTimeOffset.Parse(row.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))).ToArray();
    }

    private static DateTimeOffset? ParseTimestamp(string? value) => value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class SourceRow
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public string ConfigJson { get; set; } = "{}";
        public string Status { get; set; } = "";
        public string? LastScanAt { get; set; }
        public string? LastFullScanAt { get; set; }
        public string CreatedAt { get; set; } = "";
    }
}
