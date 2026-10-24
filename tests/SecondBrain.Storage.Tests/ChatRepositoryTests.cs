using System.Text.Json;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Chat;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class ChatRepositoryTests
{
    private static readonly AuthenticatedCredential Browser = Actor("browser", "session");
    private static readonly AuthenticatedCredential OtherBrowser = Actor("other-browser", "session");
    private static readonly AuthenticatedCredential Key = Actor("key", "api_key");
    private static readonly AuthenticatedCredential OtherKey = Actor("other-key", "api_key");
    private static readonly ChatTurnPolicy Policy = new("local", "test-model");

    [Fact]
    public async Task RequestReplayIsIdempotentAndConcurrentConversationAdmissionIsSerialized()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores);
        var request = new ChatRequest("Hello", RequestId: "request-1");
        var first = await chat.StartAsync(request, Policy, Browser);
        var repeat = await chat.StartAsync(request, Policy with { Model = "changed" }, Browser);
        Assert.True(first.Created); Assert.False(repeat.Created); Assert.Equal(first.Turn, repeat.Turn);
        await Assert.ThrowsAsync<ChatConflictException>(() => chat.StartAsync(request with { Prompt = "different" }, Policy, Browser).AsTask());
        await chat.FinishAsync(first.Turn.Id, "complete", new("Hi", []));
        var admitted = 0;
        var rejected = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            try { await chat.StartAsync(new("Next " + i, first.Turn.ConversationId), Policy, Browser); Interlocked.Increment(ref admitted); }
            catch (ChatConflictException) { Interlocked.Increment(ref rejected); }
        }));
        Assert.Equal(1, admitted); Assert.Equal(7, rejected);
    }

    [Fact]
    public async Task BrowserOwnershipIsPortableButKeysAreIsolatedAndCannotImpersonateTheInitiator()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores);
        var browser = (await chat.StartAsync(new("Browser question"), Policy, Browser)).Turn;
        Assert.Single(await chat.ListAsync(OtherBrowser));
        Assert.Equal(browser.Id, (await chat.GetTurnAsync(browser.Id, OtherBrowser)).Id);
        await Assert.ThrowsAsync<AuthorityChangedException>(() => chat.CountToolAsync(browser.Id, 0, OtherBrowser).AsTask());
        var key = (await chat.StartAsync(new("Key question"), Policy, Key)).Turn;
        Assert.Empty(await chat.ListAsync(OtherKey));
        await Assert.ThrowsAsync<ChatNotFoundException>(() => chat.GetTurnAsync(key.Id, OtherKey).AsTask());
        await Assert.ThrowsAsync<ChatNotFoundException>(() => chat.GetTurnAsync(key.Id, Browser).AsTask());
        await Assert.ThrowsAsync<ChatNotFoundException>(() => chat.GetTurnAsync(browser.Id, Key).AsTask());
    }

    [Fact]
    public async Task StoredScopesEpochAndGenerationWinOverCallerClaims()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores);
        await MigrationTestSql.WriteAsync(stores.State, "UPDATE credentials SET scopes='read' WHERE id='key'");
        await Assert.ThrowsAsync<AuthorityChangedException>(() => chat.StartAsync(new("No infer"), Policy, Key).AsTask());
        await Assert.ThrowsAsync<AuthorityChangedException>(() => chat.StartAsync(new("Wrong generation"), Policy, Browser with { Generation = 2 }).AsTask());
        await MigrationTestSql.WriteAsync(stores.State, "UPDATE meta SET value='2' WHERE key='account_epoch'");
        await Assert.ThrowsAsync<AuthorityChangedException>(() => chat.ListAsync(Browser).AsTask());
    }

    [Fact]
    public async Task RevocationBlocksTheNextProviderAndToolBoundaryButAllowsTerminalPersistence()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores);
        var turn = (await chat.StartAsync(new("Work"), Policy, Browser)).Turn;
        await MigrationTestSql.WriteAsync(stores.State, "UPDATE credentials SET revoked_at='2026-01-01T00:00:00Z' WHERE id='browser'");
        await Assert.ThrowsAsync<AuthorityChangedException>(() => chat.ReserveTokensAsync(turn.Id, "call-1", 100, Browser).AsTask());
        await Assert.ThrowsAsync<AuthorityChangedException>(() => chat.CountToolAsync(turn.Id, 0, Browser).AsTask());
        await Assert.ThrowsAsync<AuthorityChangedException>(() => chat.EventsAsync(turn.Id, 0, Browser).AsTask());
        await chat.FinishAsync(turn.Id, "partial", new("", []), "authority_changed");
        Assert.Equal("partial", (await chat.GetTurnAsync(turn.Id, OtherBrowser)).Status);
    }

    [Fact]
    public async Task EventsReplayInCommittedOrderAndRestartDoesNotRerunInterruptedWork()
    {
        using var root = new MigrationTestDataRoot();
        string turnId;
        await using (var stores = new MigrationTestStores(root.Path))
        {
            var chat = await SeedAsync(stores);
            turnId = (await chat.StartAsync(new("Resume me"), Policy, Browser)).Turn.Id;
            Assert.Equal(2, await chat.AppendAsync(turnId, "delta", JsonSerializer.SerializeToElement(new { text = "Hello" }), [], Browser));
            await chat.ReserveTokensAsync(turnId, "call", 123, Browser);
        }
        await using (var stores = new MigrationTestStores(root.Path))
        {
            await stores.Runner.MigrateAsync();
            var chat = new SqliteChatRepository(stores.State, TimeProvider.System);
            Assert.Equal(1, await chat.RecoverInterruptedAsync()); Assert.Equal(0, await chat.RecoverInterruptedAsync());
            var events = await chat.EventsAsync(turnId, 1, Browser);
            Assert.Equal(new long[] { 2, 3 }, events.Select(item => item.Sequence));
            Assert.Equal("Hello", events[0].Data!.Value.GetProperty("text").GetString());
            var recovered = await chat.GetTurnAsync(turnId, Browser);
            Assert.Equal("partial", recovered.Status); Assert.Equal("interrupted", recovered.FailureCode);
            Assert.Equal("Hello", recovered.Answer!.Text);
            Assert.Equal(123L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT charged_tokens FROM chat_model_calls"));
            Assert.Equal("settled", await MigrationTestSql.ScalarAsync(stores.State, "SELECT status FROM chat_model_calls"));
        }
    }

    [Fact]
    public async Task TokenReservationsAreAtomicAndUnknownUsageCannotRefundTheBudget()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores);
        var policy = Policy with { HourlyTokens = 150, DailyTokens = 175 };
        var first = (await chat.StartAsync(new("First"), policy, Browser)).Turn;
        var second = (await chat.StartAsync(new("Second"), policy, OtherBrowser)).Turn;
        await chat.ReserveTokensAsync(first.Id, "one", 100, Browser);
        await Assert.ThrowsAsync<ChatConflictException>(() => chat.ReserveTokensAsync(first.Id, "one", 100, Browser).AsTask());
        await Assert.ThrowsAsync<ChatLimitException>(() => chat.ReserveTokensAsync(first.Id, "two", 60, Browser).AsTask());
        await Assert.ThrowsAsync<ChatLimitException>(() => chat.ReserveTokensAsync(second.Id, "one", 100, OtherBrowser).AsTask());
        await chat.SettleTokensAsync(first.Id, "one", 30);
        await chat.ReserveTokensAsync(second.Id, "one", 100, OtherBrowser);
        await chat.SettleTokensAsync(second.Id, "one", null);
        await chat.SettleTokensAsync(second.Id, "one", 0); // A second settlement must not refund the first.
        Assert.Equal(130L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT SUM(charged_tokens) FROM chat_model_calls"));
    }

    [Fact]
    public async Task ToolAndMetadataLimitsCannotBeResetByRepeatedCalls()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores);
        var turn = (await chat.StartAsync(new("Question"), Policy with { MaxToolCalls = 2, MaxMetadataRecords = 3 }, Browser)).Turn;
        await chat.CountToolAsync(turn.Id, 2, Browser);
        await Assert.ThrowsAsync<ChatLimitException>(() => chat.CountToolAsync(turn.Id, 2, Browser).AsTask());
        await chat.CountToolAsync(turn.Id, 1, Browser);
        await Assert.ThrowsAsync<ChatLimitException>(() => chat.CountToolAsync(turn.Id, 0, Browser).AsTask());
    }

    [Fact]
    public async Task SourcesUseCurrentAuthoritativeBytesAndExposureLimitsAreCumulative()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores); var documents = Documents(stores);
        var a = await documents.CaptureAsync(new("The project codename is Blue Otter.", "First"), Authority(Browser));
        var b = await documents.CaptureAsync(new("The other codename is Red Fox.", "Second"), Authority(Browser));
        var turn = (await chat.StartAsync(new("Codename?"), Policy with { MaxReadDocuments = 1 }, Browser)).Turn;
        var request = new ChatSourceRequest(a.Document.Id, 1, a.Document.Chunks![0].Id);
        var source = Assert.Single(await chat.ExposeAsync(turn.Id, [request], Browser));
        Assert.Equal("The project codename is Blue Otter.", source.Excerpt);
        Assert.Equal(source, Assert.Single(await chat.ExposeAsync(turn.Id, [request], Browser)));
        await Assert.ThrowsAsync<ChatLimitException>(() => chat.ExposeAsync(turn.Id, [new(b.Document.Id, 1, b.Document.Chunks![0].Id)], Browser).AsTask());
        await Assert.ThrowsAsync<ChatConflictException>(() => chat.ExposeAsync(turn.Id, [request with { Revision = 9 }], Browser).AsTask());
        await Assert.ThrowsAsync<ChatConflictException>(() => chat.ExposeAsync(turn.Id, [request with { ChunkId = "invented" }], Browser).AsTask());
        Assert.Single(await chat.SourcesAsync(turn.Id, Browser));
    }

    [Fact]
    public async Task PurgeRedactsSourceAnswerAndReplayWhilePreservingUserAuthoredMessages()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores); var documents = Documents(stores);
        var document = (await documents.CaptureAsync(new("Secret codename: Blue Otter.", "Codename"), Authority(Browser))).Document;
        var turn = (await chat.StartAsync(new("What is the codename?"), Policy, Browser)).Turn;
        var source = Assert.Single(await chat.ExposeAsync(turn.Id, [new(document.Id, 1, document.Chunks![0].Id)], Browser));
        await chat.AppendAsync(turn.Id, "delta", JsonSerializer.SerializeToElement(new { text = "Blue Otter [1]" }), [], Browser);
        await chat.FinishAsync(turn.Id, "complete", CitationVerifier.Verify("Blue Otter [1]", [source]));
        await documents.MutateAsync(document.Id, 1, "purge", JsonSerializer.SerializeToElement(new { }), Authority(Browser));
        var result = await chat.GetTurnAsync(turn.Id, Browser);
        Assert.Equal("What is the codename?", result.Prompt); Assert.Null(result.Answer); Assert.True(result.Redacted);
        Assert.Empty(await chat.SourcesAsync(turn.Id, Browser));
        var events = await chat.EventsAsync(turn.Id, 0, Browser);
        Assert.All(events.Where(item => item.Kind != "started"), item => { Assert.True(item.Redacted); Assert.Null(item.Data); });
        var next = (await chat.StartAsync(new("Continue", turn.ConversationId), Policy, Browser)).Turn;
        var history = await chat.HistoryAsync(next.Id, Browser);
        Assert.Single(history); Assert.Equal("user", history[0].Role);
    }

    [Fact]
    public async Task HistoricalCitationsKeepTheirExcerptButShowThatTheDocumentWasDeleted()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores); var documents = Documents(stores);
        var document = (await documents.CaptureAsync(new("Blue Otter", "Codename"), Authority(Browser))).Document;
        var turn = (await chat.StartAsync(new("Codename?"), Policy, Browser)).Turn;
        await chat.ExposeAsync(turn.Id, [new(document.Id, 1, document.Chunks![0].Id)], Browser);
        await chat.FinishAsync(turn.Id, "complete", new("Blue Otter [1] and fabricated [2]", []));
        var before = await chat.GetTurnAsync(turn.Id, Browser);
        Assert.Equal(1, before.Answer!.InvalidCitationCount);
        Assert.Equal("current", Assert.Single(before.Answer.Citations).State);
        await documents.MutateAsync(document.Id, 1, "delete", JsonSerializer.SerializeToElement(new { }), Authority(Browser));
        var after = await chat.GetTurnAsync(turn.Id, Browser);
        Assert.Equal("deleted", Assert.Single(after.Answer!.Citations).State);
        Assert.Equal("Blue Otter", after.Answer.Citations[0].Source.Excerpt);
    }

    [Fact]
    public async Task ExposureByteLimitCountsTheCompleteSerializedSourceAndRollsBackFailure()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores); var documents = Documents(stores);
        var document = (await documents.CaptureAsync(new("Blue Otter", new string('T', 200)), Authority(Browser))).Document;
        var turn = (await chat.StartAsync(new("Codename?"), Policy with { MaxReadBytes = 200 }, Browser)).Turn;
        await Assert.ThrowsAsync<ChatLimitException>(() => chat.ExposeAsync(turn.Id, [new(document.Id, 1, document.Chunks![0].Id)], Browser).AsTask());
        Assert.Empty(await chat.SourcesAsync(turn.Id, Browser));
    }

    [Fact]
    public async Task PurgeDuringGenerationPreventsLateOutputFromReintroducingItsContents()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores); var documents = Documents(stores);
        var document = (await documents.CaptureAsync(new("Secret codename: Blue Otter.", "Codename"), Authority(Browser))).Document;
        var turn = (await chat.StartAsync(new("What is the codename?"), Policy, Browser)).Turn;
        await chat.ExposeAsync(turn.Id, [new(document.Id, 1, document.Chunks![0].Id)], Browser);
        await documents.MutateAsync(document.Id, 1, "purge", JsonSerializer.SerializeToElement(new { }), Authority(Browser));
        await Assert.ThrowsAsync<ChatConflictException>(() => chat.AppendAsync(turn.Id, "delta", JsonSerializer.SerializeToElement("Blue Otter"), [], Browser).AsTask());
        await chat.FinishAsync(turn.Id, "complete", new("Blue Otter", []));
        var result = await chat.GetTurnAsync(turn.Id, Browser);
        Assert.Equal("partial", result.Status); Assert.Equal("source_purged", result.FailureCode);
        Assert.DoesNotContain("Blue Otter", result.Answer!.Text);
    }

    private static AuthenticatedCredential Actor(string id, string kind) => new(id, kind, 1, 1, new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer, Scope.Admin }, null);
    private static IngestionAuthority Authority(AuthenticatedCredential actor) => new(actor.Id, actor.Generation, actor.AccountEpoch, actor.Id, actor.Kind);
    private static SqliteDocumentRepository Documents(MigrationTestStores stores) => new(stores.State, stores.Index, new PublicationCoordinator(stores.State, stores.Index));
    private static async Task<SqliteChatRepository> SeedAsync(MigrationTestStores stores)
    {
        await stores.Runner.MigrateAsync();
        await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO UPDATE SET value='1'");
        var auth = new AuthRepository(stores.State, TimeProvider.System);
        foreach (var actor in new[] { Browser, OtherBrowser, Key, OtherKey })
            await auth.AddAsync(new CredentialRecord { Id = actor.Id, Name = actor.Id, Kind = actor.Kind, AccountEpoch = 1, Generation = 1,
                Scopes = "read,write,infer,admin", Verifier = "fixture", Kid = "fixture", CreatedAt = AuthTime.Format(DateTimeOffset.UtcNow), SteppedUpAt = AuthTime.Format(DateTimeOffset.UtcNow) });
        return new SqliteChatRepository(stores.State, TimeProvider.System);
    }
}
