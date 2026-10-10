using System.Data.Common;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Retrieval;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class FtsMaintenanceTests
{
    [Fact]
    public async Task ConsolidationPreservesNativeScoresHighlightsAndCanonicalTieOrder()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores);
        Assert.True(await SegmentsAsync(stores.Index) > 1);
        var before = await FingerprintsAsync(stores.Index);
        await SqliteFtsMaintenance.ConsolidateAsync(stores.Index);
        Assert.Equal(1, await SegmentsAsync(stores.Index));
        Assert.Equal(before, await FingerprintsAsync(stores.Index));
        Assert.Equal(32768L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM chunks"));
    }

    [Fact]
    public async Task CancellationBetweenCommittedBatchesPropagatesAndRetryRemainsSearchable()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores); var before = await FingerprintsAsync(stores.Index);
        using var cancellation = new CancellationTokenSource();
        var interrupted = new CancelAfterCommittedWrite(stores.Index, cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqliteFtsMaintenance.ConsolidateAsync(interrupted, cancellation.Token).AsTask());
        Assert.Equal(1, interrupted.Writes);
        using (await VectorSnapshotRegistry.For(interrupted).EnterAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1))) { }
        Assert.Equal(before, await FingerprintsAsync(stores.Index));
        await SqliteFtsMaintenance.ConsolidateAsync(stores.Index);
        Assert.Equal(1, await SegmentsAsync(stores.Index));
        Assert.Equal(before, await FingerprintsAsync(stores.Index));
    }

    private static async Task SeedAsync(MigrationTestStores stores)
    {
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.Index, "INSERT INTO indexed_documents(document_id,revision,type,title,props_json,status,updated_at) VALUES('doc',1,'note','Café observatory','{}','indexed','2026-10-09')");
        for (var batch = 0; batch < 8; batch++)
        {
            var offset = batch * 4096;
            await MigrationTestSql.WriteAsync(stores.Index, $"""
                WITH RECURSIVE n(x) AS(VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<4095)
                INSERT INTO chunks(rowid,id,document_id,revision,ordinal,context,text,start_offset,end_offset,token_count,input_hash)
                SELECT {offset}+x+1,printf('chunk-%06d',{offset}+x),'doc',1,{offset}+x,'Observatory',
                    'Café mercury calibration observation alpha beta gamma delta repeated phrase stellar instrument atmospheric discovery planet spectrum.',
                    0,130,30,'hash'||({offset}+x) FROM n;
                INSERT INTO chunks_fts_source(rowid,text,context,title)
                SELECT rowid,text,context,'Café observatory' FROM chunks WHERE rowid BETWEEN {offset + 1} AND {offset + 4096};
                INSERT INTO chunks_fts(rowid,text,context,title)
                SELECT rowid,text,context,title FROM chunks_fts_source WHERE rowid BETWEEN {offset + 1} AND {offset + 4096};
                """);
        }
    }

    private static async Task<long> SegmentsAsync(IIndexStore index) =>
        Convert.ToInt64(await MigrationTestSql.ScalarAsync(index, "SELECT COUNT(DISTINCT segid) FROM chunks_fts_idx"));

    private static async Task<string[]> FingerprintsAsync(IIndexStore index)
    {
        var results = new List<string>();
        await using var lease = await index.OpenReadConnectionAsync();
        foreach (var query in new[] { "\"mercury\"*", "\"repeated phrase\"", "\"café\" OR \"calibration\"", "\"mercury\" OR \"mercury\"" })
        {
            await using var command = lease.Connection.CreateCommand();
            command.CommandText = "SELECT c.id,bm25(chunks_fts),highlight(chunks_fts,0,'[start]','[end]') FROM chunks_fts JOIN chunks c ON c.rowid=chunks_fts.rowid WHERE chunks_fts MATCH $query ORDER BY bm25(chunks_fts),c.id LIMIT 50";
            var parameter = command.CreateParameter(); parameter.ParameterName = "$query"; parameter.Value = query; command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) results.Add(query + ":" + reader.GetString(0) + ":" + BitConverter.DoubleToInt64Bits(reader.GetDouble(1)) + ":" + reader.GetString(2));
        }
        return results.ToArray();
    }

    private sealed class CancelAfterCommittedWrite(IIndexStore inner, CancellationTokenSource cancellation) : IIndexStore
    {
        public int Writes { get; private set; }
        public ValueTask<IReadConnectionLease> OpenReadConnectionAsync(CancellationToken cancellationToken = default) => inner.OpenReadConnectionAsync(cancellationToken);
        public async ValueTask<TResult> QueueWriteAsync<TResult>(Func<DbConnection, DbTransaction, CancellationToken, ValueTask<TResult>> write, CancellationToken cancellationToken = default)
        {
            var result = await inner.QueueWriteAsync(write, cancellationToken);
            Writes++; cancellation.Cancel();
            return result;
        }
    }
}
