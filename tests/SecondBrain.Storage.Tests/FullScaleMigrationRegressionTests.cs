using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class FullScaleMigrationRegressionTests
{
    [Fact]
    public async Task CacheRestorationUsesInputHashIndexAcrossSpaces()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.Index,
            "WITH RECURSIVE n(x) AS(VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<10000) INSERT INTO embedding_cache(space_id,input_hash,vector,created_at) SELECT 'fixture',printf('%064x',x),X'0000803F','2026-10-09T00:00:00Z' FROM n");
        await using var lease = await stores.Index.OpenReadConnectionAsync();
        foreach (var sql in new[]
        {
            "EXPLAIN QUERY PLAN SELECT e.vector FROM chunks c JOIN embedding_cache e ON e.input_hash=c.input_hash WHERE c.document_id='fixture'",
            "EXPLAIN QUERY PLAN SELECT c.input_hash FROM chunks c WHERE c.document_id='fixture' AND EXISTS(SELECT 1 FROM embedding_cache e WHERE e.input_hash=c.input_hash)"
        })
        {
            await using var command = lease.Connection.CreateCommand(); command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            var plans = new List<string>();
            while (await reader.ReadAsync()) plans.Add(reader.GetString(3));
            Assert.Contains(plans, plan => plan.StartsWith("SEARCH e ", StringComparison.Ordinal) && plan.Contains("USING", StringComparison.Ordinal) && plan.Contains("embedding_cache_input_hash", StringComparison.Ordinal));
            Assert.DoesNotContain(plans, plan => plan.Contains("SCAN e", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ChunkReplacementCascadeUsesChildRowidIndex()
    {
        using var root = new MigrationTestDataRoot();
        await using var stores = new MigrationTestStores(root.Path);
        await stores.Runner.MigrateAsync();
        await using var lease = await stores.Index.OpenReadConnectionAsync();
        await using var command = lease.Connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN DELETE FROM chunks WHERE document_id='fixture'";
        await using var reader = await command.ExecuteReaderAsync();
        var plans = new List<string>();
        while (await reader.ReadAsync()) plans.Add(reader.GetString(3));
        Assert.Contains(plans, plan => plan.Contains("SEARCH chunk_vectors USING COVERING INDEX chunk_vectors_chunk_rowid", StringComparison.Ordinal));
        Assert.DoesNotContain(plans, plan => plan.Contains("SCAN chunk_vectors", StringComparison.Ordinal));
    }
}
