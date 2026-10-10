using System.Globalization;
using SecondBrain.Storage.Retrieval;

namespace SecondBrain.Storage.Durability;

/// <summary>Consolidates disposable FTS segments without changing document rows or search scores.</summary>
public static class SqliteFtsMaintenance
{
    // SQLite FTS5 documents merge(-N), then merge(+N), as incremental optimize.
    // Each invocation writes roughly N FTS pages; independent transactions retain
    // the store's existing statement/transaction limits and allow other work between batches.
    // https://sqlite.org/fts5.html#the_merge_command
    private const int PagesPerBatch = 64;
    private const int MaximumBatches = 10_000;

    public static async ValueTask ConsolidateAsync(IIndexStore index, CancellationToken cancellationToken = default)
    {
        var registry = VectorSnapshotRegistry.For(index);
        for (var batch = 0; batch < MaximumBatches; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long changes;
            using (await registry.EnterAsync(cancellationToken))
            {
                changes = await index.QueueWriteAsync(async (connection, transaction, token) =>
                {
                    await using var before = connection.CreateCommand(); before.Transaction = transaction;
                    before.CommandText = "SELECT total_changes()";
                    var initial = Convert.ToInt64(await before.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
                    await using var merge = connection.CreateCommand(); merge.Transaction = transaction;
                    merge.CommandText = "INSERT INTO chunks_fts(chunks_fts,rank) VALUES('merge',$pages)";
                    var pages = merge.CreateParameter(); pages.ParameterName = "$pages";
                    pages.Value = batch == 0 ? -PagesPerBatch : PagesPerBatch; merge.Parameters.Add(pages);
                    await merge.ExecuteNonQueryAsync(token);
                    await using var after = connection.CreateCommand(); after.Transaction = transaction;
                    after.CommandText = "SELECT total_changes()";
                    return Convert.ToInt64(await after.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) - initial;
                }, cancellationToken);
            }
            // A no-op merge changes only the command row. Physical segment merges do
            // not change the committed lexical/vector generation or its derived scores.
            if (changes < 2) return;
        }
        throw new InvalidOperationException("FTS consolidation exceeded its finite work budget.");
    }
}
