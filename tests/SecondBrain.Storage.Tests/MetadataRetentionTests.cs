using Dapper;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Ingestion;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class MetadataRetentionTests
{
    [Fact]
    public async Task ExpiresOpenProposalsButConversationLifetimeWinsOverPayloadAge()
    {
        await using var f = await Fixture.Create();
        await f.Write("""
            INSERT INTO conversations(id,owner_kind,created_at,updated_at) VALUES('conversation','session',@old,@old);
            INSERT INTO turns(id,conversation_id,ordinal,status,initiating_credential_id,credential_generation,account_epoch,allowed_tools_json,started_at)
              VALUES('turn','conversation',1,'completed','admin',1,1,'[]',@old);
            """);
        foreach (var id in new[] { "open", "executing", "closed", "saved", "recent" }) await f.Payload(id);
        foreach (var (id, status, resolved, turn) in new[] { ("open", "pending", (string?)null, "turn"), ("executing", "executing", (string?)null, "turn"),
            ("closed", "discarded", f.Old, (string?)null), ("saved", "executed", f.Old, "turn"), ("recent", "discarded", f.Now, (string?)null) })
            await f.Write("""
                INSERT INTO pending_operations(id,turn_id,creator_credential_id,tool,tool_schema_version,payload_id,payload_hash,targets_json,policy_generation,status,created_at,expires_at,resolved_at)
                  VALUES(@id,@turn,'admin','test',1,@id,'hash','[]',1,@status,@old,@old,@resolved);
                INSERT INTO payload_refs VALUES(@id,'pending-operation',@id);
                """, new { id, turn, status, resolved, old = f.Old });
        await f.Write("INSERT INTO payload_refs VALUES('saved','chat-proposal','turn'); INSERT INTO payload_refs VALUES('open','chat-proposal','turn');");
        await f.Write("""
            INSERT INTO tool_executions(id,turn_id,tool,payload_id,args_hash,status,initiating_credential_id,credential_generation,account_epoch)
              VALUES('open-execution','turn','test','open','hash','pending_approval','admin',1,1),
                    ('active-execution','turn','test','executing','hash','running','admin',1,1);
            UPDATE pending_operations SET execution_id='open-execution' WHERE id='open';
            UPDATE pending_operations SET execution_id='active-execution' WHERE id='executing';
            """);
        var result = await f.Retention.RunAsync();
        Assert.Equal(1, result.ExpiredProposals); Assert.Equal(2, result.PrunedProposals);
        Assert.Equal("expired", await f.Text("SELECT status FROM pending_operations WHERE id='open'"));
        Assert.Equal("executing", await f.Text("SELECT status FROM pending_operations WHERE id='executing'"));
        Assert.Equal("expired", await f.Text("SELECT status FROM tool_executions WHERE id='open-execution'"));
        Assert.Equal("running", await f.Text("SELECT status FROM tool_executions WHERE id='active-execution'"));
        Assert.Equal(1L, await f.Count("SELECT COUNT(*) FROM chat_events WHERE turn_id='turn' AND kind='proposal'"));
        Assert.Equal(0L, await f.Count("SELECT COUNT(*) FROM payloads WHERE id='closed'"));
        Assert.Equal(1L, await f.Count("SELECT COUNT(*) FROM payloads WHERE id='saved' AND length(bytes)>0"));
        Assert.Equal(1L, await f.Count("SELECT COUNT(*) FROM pending_operations WHERE id='recent'"));
        Assert.Equal(0, (await f.Retention.RunAsync()).ExpiredProposals);
    }

    [Fact]
    public async Task LiveReferencesDirectForeignKeysAndActiveJournalPlansProtectPayloads()
    {
        await using var f = await Fixture.Create();
        foreach (var id in new[] { "orphan", "reference", "direct", "projection", "plan", "content", "recent" }) await f.Payload(id, id == "recent" ? f.Now : f.Old);
        await f.Write("""
            INSERT INTO payload_refs VALUES('reference','future-owner','retained');
            INSERT INTO batch_jobs(id,provider,remote_id,kind,status,request_payload_id,created_at,updated_at)
              VALUES('batch','provider','remote','enrich','running','direct',@old,@old);
            UPDATE payloads SET bytes=CAST('{"Finalization":{"DocumentMetadata":{"ProjectionPayloadId":"projection"}}}' AS BLOB) WHERE id='plan';
            INSERT INTO mutations(id,kind,operation_id,destination,payload_id,status,created_at,updated_at)
              VALUES('mutation','import','operation','/managed','content','prepared',@old,@old);
            INSERT INTO payload_refs VALUES('plan','mutation','mutation');
            """);
        var result = await f.Retention.RunAsync();
        Assert.Equal(1, result.OrphanPayloads);
        foreach (var id in new[] { "reference", "direct", "projection", "plan", "content", "recent" })
            Assert.Equal(1L, await f.Count("SELECT COUNT(*) FROM payloads WHERE id=@id", new { id }));
        Assert.Equal(0L, await f.Count("SELECT COUNT(*) FROM pragma_foreign_key_check"));
    }

    [Fact]
    public async Task BoundedRunsPruneExpiredIdempotencyTracesAndTheirDetachedPayloadReferences()
    {
        await using var f = await Fixture.Create();
        for (var i = 0; i < 3; i++)
        {
            await f.Payload("old" + i);
            await f.Write("INSERT INTO idempotency VALUES(@key,'admin','hash','response',@old); INSERT INTO payload_refs VALUES(@key,'idempotency',@reference);",
                new { key = "old" + i, reference = "[\"old" + i + "\",\"admin\"]", old = f.Old });
            await f.IndexWrite("INSERT INTO retrieval_traces(at,query_json,trace_json) VALUES(@old,'{}','{}')");
        }
        await f.Write("INSERT INTO idempotency VALUES('new','admin','hash','response',@now)");
        await f.IndexWrite("INSERT INTO retrieval_traces(at,query_json,trace_json) VALUES(@now,'{}','{}')");
        var total = 0;
        for (var i = 0; i < 4; i++)
        {
            var result = await f.Retention.RunAsync(limit: 1);
            Assert.InRange(result.IdempotencyRecords, 0, 1); Assert.InRange(result.RetrievalTraces, 0, 1);
            total += result.OrphanPayloads;
        }
        Assert.Equal(3, total);
        Assert.Equal(1L, await f.Count("SELECT COUNT(*) FROM idempotency"));
        await using var read = await f.Stores.Index.OpenReadConnectionAsync();
        Assert.Equal(1, await read.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM retrieval_traces"));
        Assert.False((await f.Retention.RunAsync(limit: 1)).MoreWork);
    }

    [Fact]
    public async Task ReleasingTheLastProposalOwnerDeletesItsPayloadWithoutAnExtraGracePeriod()
    {
        await using var f = await Fixture.Create(); f.Options.Value.Retention.ResolvedProposalDays = 1;
        await f.Payload("recent-proposal", "2026-10-22T12:00:00Z");
        await f.Write("""
            INSERT INTO pending_operations(id,creator_credential_id,tool,tool_schema_version,payload_id,payload_hash,targets_json,policy_generation,status,created_at,expires_at,resolved_at)
              VALUES('recent-proposal','admin','test',1,'recent-proposal','hash','[]',1,'discarded','2026-10-22T12:00:00Z',@now,'2026-10-22T12:00:00Z');
            INSERT INTO payload_refs VALUES('recent-proposal','pending-operation','recent-proposal');
            """);
        var result = await f.Retention.RunAsync();
        Assert.Equal(1, result.PrunedProposals); Assert.Equal(1, result.OrphanPayloads);
    }

    [Fact]
    public async Task RevokedActorCannotPruneAndReloadedPolicyAppliesOnTheNextRun()
    {
        await using var f = await Fixture.Create(); await f.Payload("orphan");
        await Assert.ThrowsAsync<AuthorityChangedException>(async () => await f.Retention.RunAsync(new("admin", 2, 1, "admin", "session")));
        Assert.Equal(1L, await f.Count("SELECT COUNT(*) FROM payloads WHERE id='orphan'"));
        f.Options.Value.Retention.PayloadDays = 90;
        Assert.Equal(0, (await f.Retention.RunAsync()).OrphanPayloads);
        f.Options.Value.Retention.PayloadDays = 30;
        Assert.Equal(1, (await f.Retention.RunAsync(new("admin", 1, 1, "admin", "session"))).OrphanPayloads);
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-24T12:00:00Z"); }
    private sealed class Monitor : IOptionsMonitor<SecondBrainOptions>
    {
        internal SecondBrainOptions Value { get; } = new();
        public SecondBrainOptions CurrentValue => Value;
        public SecondBrainOptions Get(string? name) => Value;
        public IDisposable? OnChange(Action<SecondBrainOptions, string?> listener) => null;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MigrationTestDataRoot root = new();
        internal MigrationTestStores Stores { get; }
        internal Monitor Options { get; } = new();
        internal SqliteMetadataRetention Retention { get; }
        internal string Now => "2026-10-24T12:00:00Z";
        internal string Old => "2026-08-01T12:00:00Z";
        private Fixture() { Stores = new(root.Path); Retention = new(Stores.State, Stores.Index, Options, new Clock()); }
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture(); await f.Stores.Runner.MigrateAsync();
            await f.Write("INSERT INTO meta VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
            await new AuthRepository(f.Stores.State, new Clock()).AddAsync(new CredentialRecord { Id = "admin", Name = "admin", Kind = "session", Generation = 1, AccountEpoch = 1,
                Scopes = "read,write,admin", Verifier = "fixture", Kid = "fixture", CreatedAt = f.Now });
            return f;
        }
        internal Task Write(string sql, object? values = null) => Stores.State.QueueWriteAsync(async (c, t, ct) => { await c.ExecuteAsync(new CommandDefinition(sql, values ?? new { old = Old, now = Now }, t, cancellationToken: ct)); return 0; }).AsTask();
        internal Task IndexWrite(string sql) => Stores.Index.QueueWriteAsync(async (c, t, ct) => { await c.ExecuteAsync(new CommandDefinition(sql, new { old = Old, now = Now }, t, cancellationToken: ct)); return 0; }).AsTask();
        internal Task Payload(string id, string? date = null) => Write("INSERT INTO payloads(id,sha256,bytes,created_at) VALUES(@id,'hash',X'73616665',@date)", new { id, date = date ?? Old });
        internal async Task<long> Count(string sql, object? values = null) { await using var read = await Stores.State.OpenReadConnectionAsync(); return await read.Connection.ExecuteScalarAsync<long>(sql, values); }
        internal async Task<string?> Text(string sql) { await using var read = await Stores.State.OpenReadConnectionAsync(); return await read.Connection.ExecuteScalarAsync<string?>(sql); }
        public async ValueTask DisposeAsync() { await Stores.DisposeAsync(); root.Dispose(); }
    }
}
