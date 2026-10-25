using Dapper;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Providers;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Chat;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using SecondBrain.Storage.Usage;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class ModelUsageTests
{
    [Fact]
    public async Task ConcurrentReservationsCannotOverspendAndSettlementIsSingleUse()
    {
        await using var f = await Fixture.Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async n =>
        { try { return await f.Usage.ReserveAsync(f.Request("parallel-" + n, 20)); } catch (ModelBudgetException) { return 0L; } }));
        Assert.Equal(5, results.Count(id => id != 0));
        var first = results.First(id => id != 0);
        await f.Usage.SettleAsync(first, new(3, 2, 1, 5));
        await f.Usage.SettleAsync(first, new(0, 0, 0, 0));
        Assert.Equal(85, (await f.Report()).DailyTokens);
        await Assert.ThrowsAsync<ModelBudgetException>(() => f.Usage.ReserveAsync(f.Request("over", 16)));
        await f.Usage.ReserveAsync(f.Request("fits", 15));
        Assert.Equal(100, (await f.Report()).DailyTokens);
    }

    [Fact]
    public async Task UnknownOrMalformedUsageKeepsItsReservationAndSurvivesRestart()
    {
        await using var f = await Fixture.Create();
        var id = await f.Usage.ReserveAsync(f.Request("unknown", 40));
        await f.Usage.SettleAsync(id, null);
        var bad = await f.Usage.ReserveAsync(f.Request("bad", 30));
        await f.Usage.SettleAsync(bad, new(30, 10, 31, 1));
        var resumed = new SqliteModelUsage(f.Stores.State, Options.Create(f.Options), f.Clock);
        Assert.Equal(70, (await resumed.ReadAsync(f.Authority, f.Clock.Now.AddDays(-1), f.Clock.Now.AddSeconds(1))).DailyTokens);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resumed.ReserveAsync(f.Request("unknown", 1)));
    }

    [Fact]
    public async Task ChatAndEmbeddingShareCapsAndDeletingAConversationDoesNotEraseUsage()
    {
        await using var f = await Fixture.Create();
        await f.Usage.ReserveAsync(f.Request("index", 30));
        var chat = new SqliteChatRepository(f.Stores.State, f.Clock);
        var turn = (await chat.StartAsync(new("Question"), new("local", "chat", HourlyTokens: 100, DailyTokens: 100), f.Actor)).Turn;
        await Assert.ThrowsAsync<ChatLimitException>(() => chat.ReserveTokensAsync(turn.Id, "over", 71, f.Actor).AsTask());
        await chat.ReserveTokensAsync(turn.Id, "fits", 70, f.Actor);
        await chat.SettleUsageAsync(turn.Id, "fits", new(8, 2, 3, 10));
        await chat.FinishAsync(turn.Id, "complete", new("Answer", []));
        await chat.DeleteConversationAsync(turn.ConversationId, f.Actor);
        Assert.Equal(40, (await f.Report()).DailyTokens);
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(f.Stores.State, "SELECT COUNT(*) FROM chat_usage_history"));
        var chatRow = Assert.Single((await f.Report()).Rows, row => row.Role == "chat");
        Assert.Equal(3, chatRow.CachedInputTokens); Assert.Equal(8, chatRow.InputTokens);
    }

    [Fact]
    public async Task LegacyChargesStillConsumeCapsAndHourlyWindowsExpireIndependently()
    {
        await using var f = await Fixture.Create();
        await f.Stores.State.QueueWriteAsync(async (c, t, ct) => await c.ExecuteAsync(new CommandDefinition(
            "INSERT INTO chat_usage_history(credential_id,charged_tokens,created_at) VALUES('actor',80,@at)", new { at = AuthTime.Format(f.Clock.Now) }, t, cancellationToken: ct)));
        await Assert.ThrowsAsync<ModelBudgetException>(() => f.Usage.ReserveAsync(f.Request("hourly", 21)));
        f.Clock.Now = f.Clock.Now.AddHours(2);
        await f.Usage.ReserveAsync(f.Request("next-hour", 30));
        Assert.Equal(110, (await f.Report()).DailyTokens);
        Assert.Contains((await f.Report()).Rows, row => row.Role == "legacy-chat" && row.ChargedTokens == 80);
        f.Clock.Now = f.Clock.Now.AddDays(1);
        Assert.Equal(0, (await f.Report()).DailyTokens);
    }

    [Fact]
    public async Task AdmittedDocumentWorkRetainsItsCredentialAndJobAfterRevocation()
    {
        await using var f = await Fixture.Create();
        var documents = new SqliteDocumentRepository(f.Stores.State, f.Stores.Index, new PublicationCoordinator(f.Stores.State, f.Stores.Index));
        var document = (await documents.CaptureAsync(new("Document to embed", "Attribution"), f.Authority)).Document;
        await MigrationTestSql.WriteAsync(f.Stores.State, "UPDATE credentials SET revoked_at='2026-01-01T00:00:00Z' WHERE id='actor'");
        await Assert.ThrowsAsync<AuthorityChangedException>(() => f.Usage.ReserveAsync(f.Request("interactive", 1)));
        var id = await f.Usage.ReserveAsync(new("admitted", "local", "embed", "embed", 10, DocumentId: document.Id, Revision: document.Revision));
        await f.Usage.SettleAsync(id, new(4, 0, 0, 4));
        await using var read = await f.Stores.State.OpenReadConnectionAsync();
        Assert.Equal("actor", await read.Connection.ExecuteScalarAsync<string>("SELECT credential_id FROM usage WHERE id=@id", new { id }));
        Assert.NotNull(await read.Connection.ExecuteScalarAsync<string>("SELECT job_id FROM usage WHERE id=@id", new { id }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Usage.ReserveAsync(new("stale", "local", "embed", "embed", 1, DocumentId: document.Id, Revision: 999)));
    }

    [Fact]
    public async Task ReportRequiresCurrentAdminAndRejectsUnboundedWindows()
    {
        await using var f = await Fixture.Create();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Usage.ReadAsync(f.Authority, f.Clock.Now.AddDays(-32), f.Clock.Now));
        await MigrationTestSql.WriteAsync(f.Stores.State, "UPDATE credentials SET scopes='read' WHERE id='actor'");
        await Assert.ThrowsAsync<AuthorityChangedException>(() => f.Report());
    }

    [Fact]
    public async Task MemberEmbeddingUsesTheParentImportCredentialAndItsOwnJobId()
    {
        await using var f = await Fixture.Create();
        var documents = new SqliteDocumentRepository(f.Stores.State, f.Stores.Index, new PublicationCoordinator(f.Stores.State, f.Stores.Index));
        var parent = (await documents.CaptureAsync(new("Imported parent", "Container"), f.Authority)).Document;
        await f.Stores.State.QueueWriteAsync(async (c, t, ct) => await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO documents(id,source_id,type,type_origin,title,provenance,content_hash,status,created_at,updated_at,container_document_id)
              SELECT 'member',source_id,'calendar_event','format','Member',provenance,'member-hash','indexed_partial',created_at,updated_at,id FROM documents WHERE id=@parent;
            INSERT INTO jobs(id,kind,document_id,revision,generation_json,account_epoch,status,created_at,updated_at)
              VALUES('member-embedding','embed','member',1,'{}',1,'queued',@now,@now);
            """, new { parent = parent.Id, now = AuthTime.Format(f.Clock.Now) }, t, cancellationToken: ct)));
        var id = await f.Usage.ReserveAsync(new("member-call", "local", "embed", "embed", 10, DocumentId: "member", Revision: 1));
        await using var read = await f.Stores.State.OpenReadConnectionAsync();
        Assert.Equal("actor", await read.Connection.ExecuteScalarAsync<string>("SELECT credential_id FROM usage WHERE id=@id", new { id }));
        Assert.Equal("member-embedding", await read.Connection.ExecuteScalarAsync<string>("SELECT job_id FROM usage WHERE id=@id", new { id }));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(DateTimeOffset.UtcNow.UtcDateTime.Date.AddHours(12), TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MigrationTestDataRoot root = new();
        public MigrationTestStores Stores { get; private set; } = null!;
        public Clock Clock { get; } = new();
        public SecondBrainOptions Options { get; } = new() { Budgets = new() { DailyTokens = 200 }, Limits = new() { PerCredential = new() { ModelTokensPerHour = 100 } } };
        public SqliteModelUsage Usage { get; private set; } = null!;
        public AuthenticatedCredential Actor { get; } = new("actor", "session", 1, 1, new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer, Scope.Admin }, null);
        public IngestionAuthority Authority => new("actor", 1, 1, "actor", "session");
        public ModelUsageRequest Request(string call, long tokens) => new(call, "local", "model", "embed", tokens, Authority);
        public Task<ModelUsageReport> Report() => Usage.ReadAsync(Authority, Clock.Now.AddDays(-1), Clock.Now.AddSeconds(1));
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); f.Stores = new(f.root.Path); await f.Stores.Runner.MigrateAsync();
            await MigrationTestSql.WriteAsync(f.Stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
            await new AuthRepository(f.Stores.State, f.Clock).AddAsync(new CredentialRecord
            { Id = "actor", Name = "actor", Kind = "session", AccountEpoch = 1, Generation = 1, Scopes = "read,write,infer,admin", Verifier = "test", Kid = "test", CreatedAt = AuthTime.Format(f.Clock.Now) });
            f.Usage = new(f.Stores.State, Microsoft.Extensions.Options.Options.Create(f.Options), f.Clock); return f;
        }
        public async ValueTask DisposeAsync() { await Stores.DisposeAsync(); root.Dispose(); }
    }
}
