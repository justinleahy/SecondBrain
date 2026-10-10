using System.Data.Common;
using System.Reflection;
using System.Data;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using SecondBrain.Core.Retrieval;
using SecondBrain.Storage.Retrieval;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class BoundedLexicalStateTests
{
    private static string Sanitize(string query) => (string)typeof(SqliteRetrievalRepository).GetMethod("SanitizeFts", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [query])!;
    private static string Filter(bool article) => "d.status NOT IN ('removed_from_source','deleted','failed','stale')" + (article ? " AND d.type='article'" : "");

    [Theory]
    [InlineData(12)]
    [InlineData(37)]
    [InlineData(103)]
    public async Task DerivedRankingIsNativeBitExactForVariedPhrasesPrefixesDuplicatesAndFilters(int seed)
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        await SeedAsync(stores, seed);
        var state = new LexicalState(4 * 1024 * 1024);
        var queries = new[] { "red giant", "\"red giant\" telescope", "café cafe", "telesc* telescope telesc*", "red red red", "\"red giant\" \"red giant\"", "\"alpha beta\" alpha_beta", "東京 orbit", "absentnomatch", "red* giant cafe telesc* 東京 \"alpha beta\"" };
        await using var snapshot = await stores.Index.OpenReadConnectionAsync(); await using var transaction = await snapshot.Connection.BeginTransactionAsync();
        foreach (var raw in queries)
        foreach (var filtered in new[] { false, true })
        {
            var query = Sanitize(raw); var filter = Filter(filtered);
            var native = await NativeAsync(snapshot.Connection, transaction, query, filter);
            var derived = await state.TryRankAsync(snapshot.Connection, transaction, 1, query, filter, [], true, TimeSpan.FromSeconds(4), default);
            Assert.NotNull(derived); AssertExact(native, derived);
            var warmed = await state.TryRankAsync(snapshot.Connection, transaction, 1, query, filter, [], false, TimeSpan.Zero, default);
            Assert.NotNull(warmed); AssertExact(native, warmed);
        }
        Assert.InRange(state.PeakBytes, 1, 4 * 1024 * 1024);
        // The fixture deliberately contains indexed_partial rows and no vector/space tables populated.
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.Index, "SELECT COUNT(*) FROM chunk_vectors"));
    }

    [Fact]
    public async Task EveryGenerationInvalidatesGlobalStatisticsAndRecoversWithoutForcedCollection()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await SeedAsync(stores, 12);
        var state = new LexicalState(4 * 1024 * 1024); var query = Sanitize("red giant cafe");
        long[]? previousScores = null;
        for (var generation = 1; generation <= 4; generation++)
        {
            await using (var snapshot = await stores.Index.OpenReadConnectionAsync())
            await using (var transaction = await snapshot.Connection.BeginTransactionAsync())
            {
                var native = await NativeAsync(snapshot.Connection, transaction, query, Filter(false));
                var derived = await state.TryRankAsync(snapshot.Connection, transaction, generation, query, Filter(false), [], true, TimeSpan.FromSeconds(4), default);
                Assert.NotNull(derived); AssertExact(native, derived);
                var bits = native.Select(hit => BitConverter.DoubleToInt64Bits(hit.Score)).ToArray();
                if (previousScores is not null) Assert.NotEqual(previousScores, bits);
                previousScores = bits;
                Assert.InRange(state.AccountedBytes, 1, 4 * 1024 * 1024);
            }
            // This document matches none of the query primitives but changes global N/average length.
            var row = 100000 + generation;
            await MigrationTestSql.WriteAsync(stores.Index, $$"""
                INSERT INTO indexed_documents(document_id,revision,type,title,props_json,status,updated_at)
                VALUES('new-{{generation}}',1,'note','Unrelated','{}','indexed_partial','2026-10-09');
                INSERT INTO chunks(rowid,id,document_id,revision,ordinal,context,text,start_offset,end_offset,token_count,input_hash)
                VALUES({{row}},'new-chunk-{{generation}}','new-{{generation}}',1,0,'Unrelated','nonmatching',0,11,8,'hash');
                INSERT INTO chunks_fts_source VALUES({{row}},'{{string.Join(' ', Enumerable.Repeat("nonmatching", 1000))}}','Unrelated','Unrelated');
                INSERT INTO chunks_fts(rowid,text,context,title) SELECT rowid,text,context,title FROM chunks_fts_source WHERE rowid={{row}};
                """);
        }
    }

    [Fact]
    public async Task EvictionKeepsFrequentlyUsedPrimitivesAndRecoversWithoutCollection()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await SeedAsync(stores, 12);
        await using var snapshot = await stores.Index.OpenReadConnectionAsync(); await using var transaction = await snapshot.Connection.BeginTransactionAsync();
        var probe = new LexicalState(4 * 1024 * 1024);
        var absent = Sanitize("absentnomatch");
        Assert.NotNull(await probe.TryRankAsync(snapshot.Connection, transaction, 1, absent, Filter(false), [], true, TimeSpan.FromSeconds(4), default));
        var fixedBytes = probe.AccountedBytes - 512 - absent.Length * 2;
        async Task<long> Payload(string raw)
        {
            var phrase = Sanitize(raw);
            await using var command = Command(snapshot.Connection, transaction, "SELECT COUNT(*) FROM chunks_fts WHERE chunks_fts MATCH $query", ("$query", phrase));
            return Convert.ToInt64(await command.ExecuteScalarAsync()) * 12 + 512 + phrase.Length * 2;
        }
        var largest = Math.Max(await Payload("red"), Math.Max(await Payload("giant"), await Payload("cafe")));
        var budget = fixedBytes + largest * 2 + 32 + 512 * 1024;
        var state = new LexicalState(budget);
        async Task<IReadOnlyList<Hit>?> Rank(string raw, bool fill = true, int generation = 1)
            => await state.TryRankAsync(snapshot.Connection, transaction, generation, Sanitize(raw), Filter(false), [], fill, TimeSpan.FromSeconds(4), default);
        Assert.NotNull(await Rank("red")); Assert.NotNull(await Rank("giant")); Assert.NotNull(await Rank("red", false));
        Assert.NotNull(await Rank("cafe")); Assert.NotNull(await Rank("red", false)); Assert.Null(await Rank("giant", false));
        var recovered = await Rank("giant"); Assert.NotNull(recovered);
        AssertExact(await NativeAsync(snapshot.Connection, transaction, Sanitize("giant"), Filter(false)), recovered);
        Assert.NotNull(await Rank("red", generation: 2));
        Assert.InRange(state.PeakBytes, 1, budget);
    }

    [Fact]
    public async Task CapacityRefusalAndInternalDeadlineLeaveNativeTransactionUsable()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await SeedAsync(stores, 37);
        await using var snapshot = await stores.Index.OpenReadConnectionAsync(); await using var transaction = await snapshot.Connection.BeginTransactionAsync();
        var query = Sanitize("red giant cafe"); var filter = Filter(false);
        var native = await NativeAsync(snapshot.Connection, transaction, query, filter);
        var tooSmall = new LexicalState(1024);
        Assert.Null(await tooSmall.TryRankAsync(snapshot.Connection, transaction, 1, query, filter, [], true, TimeSpan.FromSeconds(4), default));
        Assert.InRange(tooSmall.AccountedBytes, 0, 1024);
        var expired = new LexicalState(4 * 1024 * 1024, TimeSpan.FromTicks(1));
        Assert.Null(await expired.TryRankAsync(snapshot.Connection, transaction, 1, query, filter, [], true, TimeSpan.FromSeconds(4), default));
        AssertExact(native, await NativeAsync(snapshot.Connection, transaction, query, filter));
        var retry = new LexicalState(4 * 1024 * 1024);
        var recovered = await retry.TryRankAsync(snapshot.Connection, transaction, 1, query, filter, [], true, TimeSpan.FromSeconds(4), default);
        Assert.NotNull(recovered); AssertExact(native, recovered);
    }

    [Fact]
    public async Task CancellationInsidePostingFillReleasesItsReservationAndRetryIsNativeExact()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await SeedAsync(stores, 37);
        await using var snapshot = await stores.Index.OpenReadConnectionAsync(); await using var transaction = await snapshot.Connection.BeginTransactionAsync();
        using var cancellation = new CancellationTokenSource();
        var interrupted = new InterruptConnection(snapshot.Connection, cancellation);
        var state = new LexicalState(4 * 1024 * 1024); var query = Sanitize("red giant");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.TryRankAsync(interrupted, transaction, 1, query, Filter(false), [], true, TimeSpan.FromSeconds(4), cancellation.Token).AsTask());
        Assert.True(interrupted.InterruptedRows > 0); Assert.Equal(0, state.PrimitiveLoads);
        var recovered = await state.TryRankAsync(snapshot.Connection, transaction, 1, query, Filter(false), [], true, TimeSpan.FromSeconds(4), default);
        Assert.NotNull(recovered); AssertExact(await NativeAsync(snapshot.Connection, transaction, query, Filter(false)), recovered);
        Assert.InRange(state.AccountedBytes, 1, 4 * 1024 * 1024);
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndStaleSemanticSnapshotSkipsAllFills()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path); await SeedAsync(stores, 103);
        await using var snapshot = await stores.Index.OpenReadConnectionAsync(); await using var transaction = await snapshot.Connection.BeginTransactionAsync();
        var state = new LexicalState(4 * 1024 * 1024); var query = Sanitize("red giant");
        Assert.Null(await state.TryRankAsync(snapshot.Connection, transaction, 1, query, Filter(false), [], false, TimeSpan.FromSeconds(4), default));
        Assert.Equal(0, state.PrimitiveLoads); Assert.Equal(0, state.AccountedBytes);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.TryRankAsync(snapshot.Connection, transaction, 1, query, Filter(false), [], true, TimeSpan.FromSeconds(4), cancellation.Token).AsTask());
        var recovered = await state.TryRankAsync(snapshot.Connection, transaction, 1, query, Filter(false), [], true, TimeSpan.FromSeconds(4), default);
        Assert.NotNull(recovered); AssertExact(await NativeAsync(snapshot.Connection, transaction, query, Filter(false)), recovered);
    }

    private static void AssertExact(IReadOnlyList<Hit> expected, IReadOnlyList<Hit> actual)
    {
        Assert.Equal(expected.Select(hit => hit.Id), actual.Select(hit => hit.Id));
        Assert.Equal(expected.Select(hit => hit.Rowid), actual.Select(hit => hit.Rowid));
        Assert.Equal(expected.Select(hit => BitConverter.DoubleToInt64Bits(hit.Score)), actual.Select(hit => BitConverter.DoubleToInt64Bits(hit.Score)));
    }
    private static async Task<IReadOnlyList<Hit>> NativeAsync(DbConnection connection, DbTransaction transaction, string query, string filter)
    {
        await using var command = Command(connection, transaction,
            "SELECT c.id,c.rowid,c.document_id,bm25(chunks_fts) FROM chunks_fts JOIN chunks c ON c.rowid=chunks_fts.rowid JOIN indexed_documents d ON d.document_id=c.document_id WHERE chunks_fts MATCH $query AND " + filter + " ORDER BY bm25(chunks_fts),c.id", ("$query", query));
        await using var reader = await command.ExecuteReaderAsync(); var hits = new List<Hit>(); var counts = new Dictionary<string, int>();
        while (await reader.ReadAsync())
        {
            var document = reader.GetString(2); counts.TryGetValue(document, out var count); if (count == 3) continue;
            counts[document] = count + 1; hits.Add(new(reader.GetString(0), reader.GetInt64(1), 0, reader.GetDouble(3))); if (hits.Count == 50) break;
        }
        return hits;
    }
    private static async Task SeedAsync(MigrationTestStores stores, int seed)
    {
        await stores.Runner.MigrateAsync(); var random = new Random(seed);
        await stores.Index.QueueWriteAsync(async (connection, transaction, cancellationToken) =>
        {
            await using (var documents = Command(connection, transaction, """
                WITH RECURSIVE n(x) AS(VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<73)
                INSERT INTO indexed_documents(document_id,revision,type,title,props_json,status,updated_at)
                SELECT printf('doc-%03d',x),1,CASE WHEN x%4=0 THEN 'article' ELSE 'note' END,'Fixture','{}',
                  CASE WHEN x%19=0 THEN 'removed_from_source' WHEN x%11=0 THEN 'indexed_partial' ELSE 'indexed' END,'2026-10-09' FROM n;
                """)) await documents.ExecuteNonQueryAsync(cancellationToken);
            var words = new[] { "red", "giant", "red giant", "café", "cafe\u0301", "telescope", "telescopes", "telescopic", "東京", "orbit", "alpha_beta", "alpha", "beta", "rare", "résumé" };
            for (var row = 0; row < 512; row++)
            {
                var text = string.Join(' ', Enumerable.Range(0, random.Next(3, 50)).Select(_ => words[random.Next(words.Length)])).Normalize();
                await using var command = Command(connection, transaction, """
                    INSERT INTO chunks(rowid,id,document_id,revision,ordinal,context,text,start_offset,end_offset,token_count,input_hash)
                    VALUES($rowid,$id,$doc,1,$ordinal,$context,$text,0,length($text),8,'hash');
                    INSERT INTO chunks_fts_source VALUES($rowid,$text,$context,$title);
                    INSERT INTO chunks_fts(rowid,text,context,title) VALUES($rowid,$text,$context,$title);
                    """, ("$rowid", row * 3L + 1), ("$id", $"chunk-{511-row:0000}"), ("$doc", $"doc-{row/7:000}"), ("$ordinal", row % 7),
                    ("$context", words[random.Next(words.Length)]), ("$text", text), ("$title", words[random.Next(words.Length)]));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            return 0;
        });
    }

    private sealed record Hit(string Id, long Rowid, int Document, double Score);

    // Narrow reflection access follows the existing internal retrieval test precedent.
    // Production visibility remains unchanged; no friend assembly is required.
    private sealed class LexicalState
    {
        private static readonly Type StateType = typeof(SqliteRetrievalRepository).Assembly.GetType("SecondBrain.Storage.Retrieval.BoundedLexicalState", true)!;
        private readonly object state;
        public LexicalState(long budget, TimeSpan? fillBudget = null)
            => state = Activator.CreateInstance(StateType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [budget, fillBudget], null)!;
        public long AccountedBytes => (long)StateType.GetProperty("AccountedBytes")!.GetValue(state)!;
        public long PeakBytes => (long)StateType.GetProperty("PeakBytes")!.GetValue(state)!;
        public int PrimitiveLoads => (int)StateType.GetProperty("PrimitiveLoads")!.GetValue(state)!;
        public async ValueTask<IReadOnlyList<Hit>?> TryRankAsync(DbConnection connection, DbTransaction transaction,
            long generation, string query, string filter, (string Name, object? Value)[] parameters,
            bool allowFill, TimeSpan fillRoom, CancellationToken cancellationToken)
        {
            var pending = StateType.GetMethod("TryRankAsync")!.Invoke(state,
                [connection, transaction, generation, query, filter, parameters, allowFill, fillRoom, cancellationToken])!;
            var task = (Task)pending.GetType().GetMethod("AsTask")!.Invoke(pending, null)!;
            await task;
            if (task.GetType().GetProperty("Result")!.GetValue(task) is not IEnumerable result) return null;
            return result.Cast<object>().Select(value =>
            {
                var type = value.GetType();
                return new Hit((string)type.GetProperty("Id")!.GetValue(value)!, (long)type.GetProperty("Rowid")!.GetValue(value)!,
                    (int)type.GetProperty("Document")!.GetValue(value)!, (double)type.GetProperty("Score")!.GetValue(value)!);
            }).ToArray();
        }
    }

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            var value = command.CreateParameter(); value.ParameterName = parameter.Name; value.Value = parameter.Value ?? DBNull.Value;
            command.Parameters.Add(value);
        }
        return command;
    }

    private sealed class InterruptConnection(DbConnection inner, CancellationTokenSource cancellation) : DbConnection
    {
        public int InterruptedRows { get; private set; }
        [AllowNull] public override string ConnectionString { get => inner.ConnectionString; set => throw new NotSupportedException(); }
        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => inner.State;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => throw new NotSupportedException();
        public override void Open() => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new InterruptCommand(inner.CreateCommand(), () => { InterruptedRows++; if (InterruptedRows == 100) cancellation.Cancel(); });
    }
    private sealed class InterruptCommand(DbCommand inner, Action onRead) : DbCommand
    {
        [AllowNull] public override string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
        public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
        public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
        public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
        public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
        protected override DbConnection? DbConnection { get => inner.Connection; set => inner.Connection = value; }
        protected override DbTransaction? DbTransaction { get => inner.Transaction; set => inner.Transaction = value; }
        protected override DbParameterCollection DbParameterCollection => inner.Parameters;
        public override void Cancel() => inner.Cancel();
        public override int ExecuteNonQuery() => inner.ExecuteNonQuery();
        public override object? ExecuteScalar() => inner.ExecuteScalar();
        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) => inner.ExecuteScalarAsync(cancellationToken);
        public override void Prepare() => inner.Prepare();
        protected override DbParameter CreateDbParameter() => inner.CreateParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => Wrap(inner.ExecuteReader(behavior));
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) => Wrap(await inner.ExecuteReaderAsync(behavior, cancellationToken));
        private DbDataReader Wrap(DbDataReader reader) => CommandText.StartsWith("SELECT rowid,bm25(chunks_fts)", StringComparison.Ordinal) ? new InterruptReader(reader, onRead) : reader;
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class InterruptReader(DbDataReader inner, Action onRead) : DbDataReader
    {
        public override async Task<bool> ReadAsync(CancellationToken cancellationToken) { var result = await inner.ReadAsync(cancellationToken); if (result) onRead(); return result; }
        public override bool Read() { var result = inner.Read(); if (result) onRead(); return result; }
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override bool NextResult() => inner.NextResult();
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
