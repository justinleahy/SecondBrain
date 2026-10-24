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
    public async Task DeleteRemovesTranscriptSourcesEventsAndToolsButKeepsDocumentsAndOtherConversations()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores); var documents = Documents(stores);
        var document = (await documents.CaptureAsync(new("Blue Otter", "Source"), Authority(Browser))).Document;
        var turn = (await chat.StartAsync(new("Forget this private question", RequestId: "deleted-request"), Policy, Browser)).Turn;
        await chat.ExposeAsync(turn.Id, [new(document.Id, 1, document.Chunks![0].Id)], Browser);
        var execution = await chat.BeginToolAsync(turn.Id, "search", JsonSerializer.SerializeToElement(new { query = "private" }), 1, Browser);
        await chat.CompleteToolAsync(execution, JsonSerializer.SerializeToElement(new { answer = "temporary result" }), [], Browser);
        await chat.FinishAsync(turn.Id, "complete", new("Forget this private answer [1]", []));
        var other = (await chat.StartAsync(new("Keep this conversation"), Policy, Browser)).Turn;
        await chat.FinishAsync(other.Id, "complete", new("Keep this answer", []));

        await chat.DeleteConversationAsync(turn.ConversationId, OtherBrowser);

        Assert.Equal(other.ConversationId, Assert.Single(await chat.ListAsync(Browser)).Id);
        await Assert.ThrowsAsync<ChatNotFoundException>(() => chat.GetTurnAsync(turn.Id, Browser).AsTask());
        await Assert.ThrowsAsync<ChatNotFoundException>(() => chat.EventsAsync(turn.Id, 0, Browser).AsTask());
        Assert.NotNull(await documents.GetAsync(document.Id, true));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, $"SELECT COUNT(*) FROM payload_refs WHERE referrer_id='{turn.Id}'"));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, $"SELECT COUNT(*) FROM chat_sources WHERE turn_id='{turn.Id}'"));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, $"SELECT COUNT(*) FROM tool_executions WHERE turn_id='{turn.Id}'"));
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM payloads WHERE CAST(bytes AS TEXT) LIKE '%Forget this private%'"));
        await Assert.ThrowsAsync<ChatConflictException>(() => chat.StartAsync(new("Forget this private question", RequestId: "deleted-request"), Policy, Browser).AsTask());
    }

    [Fact]
    public async Task DeleteRetainsHourlyAndGlobalDailyTokenCharges()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores);
        var policy = Policy with { HourlyTokens = 50, DailyTokens = 200 };
        var turn = (await chat.StartAsync(new("Charge"), policy, Browser)).Turn;
        await chat.ReserveTokensAsync(turn.Id, "call", 40, Browser);
        await chat.SettleTokensAsync(turn.Id, "call", 35);
        await chat.FinishAsync(turn.Id, "complete", new("Answer", []));
        await chat.DeleteConversationAsync(turn.ConversationId, Browser);
        var next = (await chat.StartAsync(new("Again"), policy, Browser)).Turn;
        await Assert.ThrowsAsync<ChatLimitException>(() => chat.ReserveTokensAsync(next.Id, "hour", 16, Browser).AsTask());
        var keyTurn = (await chat.StartAsync(new("Global budget"), policy with { DailyTokens = 40 }, Key)).Turn;
        await Assert.ThrowsAsync<ChatLimitException>(() => chat.ReserveTokensAsync(keyTurn.Id, "day", 6, Key).AsTask());
        Assert.Equal(35L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT SUM(charged_tokens) FROM chat_usage_history"));
    }

    [Fact]
    public async Task DeleteRequiresCurrentWriteAuthorityOwnershipAndNoRunningTurn()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores);
        var turn = (await chat.StartAsync(new("Key conversation"), Policy, Key)).Turn;
        await Assert.ThrowsAsync<ChatNotFoundException>(() => chat.DeleteConversationAsync(turn.ConversationId, OtherKey).AsTask());
        await Assert.ThrowsAsync<ChatConflictException>(() => chat.DeleteConversationAsync(turn.ConversationId, Key).AsTask());
        await chat.FinishAsync(turn.Id, "complete", new("Done", []));
        await MigrationTestSql.WriteAsync(stores.State, "UPDATE credentials SET scopes='read,infer' WHERE id='key'");
        await Assert.ThrowsAsync<AuthorityChangedException>(() => chat.DeleteConversationAsync(turn.ConversationId, Key).AsTask());
        Assert.Single(await chat.ListAsync(Key));
        await MigrationTestSql.WriteAsync(stores.State, "UPDATE credentials SET scopes='write' WHERE id='key'");
        await chat.DeleteConversationAsync(turn.ConversationId, Key);
        await MigrationTestSql.WriteAsync(stores.State, "UPDATE credentials SET scopes='read' WHERE id='key'");
        Assert.Empty(await chat.ListAsync(Key));
    }

    [Fact]
    public async Task DeleteRemovesCompactedHistoryAndPreservesPayloadsOwnedElsewhere()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores); var policy = Policy with { MaxHistoryMessages = 1 };
        var first = (await chat.StartAsync(new("Older message"), policy, Browser)).Turn;
        await chat.FinishAsync(first.Id, "complete", new("Older answer", []));
        var next = (await chat.StartAsync(new("Newest message", first.ConversationId), policy, Browser)).Turn;
        await chat.HistoryAsync(next.Id, Browser);
        await chat.FinishAsync(next.Id, "complete", new("Newest answer", []));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM chat_summaries"));
        await MigrationTestSql.WriteAsync(stores.State, $"INSERT INTO payload_refs(payload_id,referrer_kind,referrer_id) SELECT user_payload_id,'retained-fixture','shared' FROM chat_turn_details WHERE turn_id='{first.Id}'");
        await chat.DeleteConversationAsync(first.ConversationId, Browser);
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM chat_summaries"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM payloads"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM payload_refs WHERE referrer_kind='retained-fixture'"));
    }

    [Fact]
    public async Task ToolResultsAreDurableAndTheManifestIncludesUncitedSources()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores); var documents = Documents(stores);
        var document = (await documents.CaptureAsync(new("Blue Otter", "Note"), Authority(Browser))).Document;
        var turn = (await chat.StartAsync(new("Question"), Policy, Browser)).Turn;
        var execution = await chat.BeginToolAsync(turn.Id, "search", JsonSerializer.SerializeToElement(new { query = "codename" }), 1, Browser);
        await chat.ExposeAsync(turn.Id, [new(document.Id, 1, document.Chunks![0].Id)], Browser);
        await chat.CompleteToolAsync(execution, JsonSerializer.SerializeToElement(new { answer = "Blue Otter" }), [document.Id], Browser);
        await Assert.ThrowsAsync<ChatConflictException>(() => chat.CompleteToolAsync(execution, JsonSerializer.SerializeToElement(new { answer = "duplicate" }), [], Browser).AsTask());
        await chat.ReserveTokensAsync(turn.Id, "call", 100, Browser); await chat.SettleTokensAsync(turn.Id, "call", 25);
        var manifest = await chat.ManifestAsync(turn.Id, Browser);
        Assert.Equal(document.Id, Assert.Single(manifest.Documents).DocumentId);
        Assert.Equal(25, manifest.ChargedTokens); Assert.True(manifest.ToolResultBytes > 0);
        Assert.Equal("committed", await MigrationTestSql.ScalarAsync(stores.State, "SELECT status FROM tool_executions"));
        var activity = Assert.Single(await chat.ActivityAsync(turn.Id, Browser));
        Assert.Equal(execution, activity.Id); Assert.Equal("committed", activity.Status);
        var tool = Assert.Single(await chat.EventsAsync(turn.Id, 0, Browser), item => item.Kind == "tool");
        Assert.Equal(execution, tool.Data!.Value.GetProperty("executionId").GetString());
    }

    [Fact]
    public async Task ToolLedgerEnforcesFrozenNamesAndCumulativeLimits()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores);
        var turn = (await chat.StartAsync(new("Read", Mode: "ask"), Policy with { MaxToolCalls = 1 }, Browser)).Turn;
        await Assert.ThrowsAsync<ChatToolNotAllowedException>(() => chat.BeginToolAsync(turn.Id, "create_document", JsonSerializer.SerializeToElement(new { }), 0, Browser).AsTask());
        var execution = await chat.BeginToolAsync(turn.Id, "search", JsonSerializer.SerializeToElement(new { query = "x" }), 1, Browser);
        await chat.FailToolAsync(execution, "failed");
        await Assert.ThrowsAsync<ChatLimitException>(() => chat.BeginToolAsync(turn.Id, "search", JsonSerializer.SerializeToElement(new { query = "x" }), 1, Browser).AsTask());
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(stores.State, "SELECT COUNT(*) FROM tool_executions"));
    }

    [Fact]
    public async Task HistoryCompactionPreservesLineageAndPurgeRedactsTheSummary()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores); var documents = Documents(stores);
        var document = (await documents.CaptureAsync(new("Blue Otter", "Note"), Authority(Browser))).Document;
        var policy = Policy with { MaxHistoryMessages = 2, MaxHistoryBytes = 2048 };
        var first = (await chat.StartAsync(new("Find the codename"), policy, Browser)).Turn;
        var source = Assert.Single(await chat.ExposeAsync(first.Id, [new(document.Id, 1, document.Chunks![0].Id)], Browser));
        await chat.FinishAsync(first.Id, "complete", CitationVerifier.Verify("Blue Otter [1]", [source]));
        var second = (await chat.StartAsync(new("Continue", first.ConversationId), policy, Browser)).Turn;
        await chat.FinishAsync(second.Id, "complete", new("Next topic.", []));
        var third = (await chat.StartAsync(new("What next?", first.ConversationId), policy, Browser)).Turn;
        var history = await chat.HistoryAsync(third.Id, Browser);
        var summary = Assert.Single(history, item => item.Role == "system");
        Assert.Contains("Blue Otter", summary.Text); Assert.Contains(document.Id, summary.DocumentReferences);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(string.Concat(history.Select(item => item.Text))), 1, policy.MaxHistoryBytes);
        await chat.FinishAsync(third.Id, "complete", new("Continue.", []));
        await documents.MutateAsync(document.Id, 1, "purge", JsonSerializer.SerializeToElement(new { }), Authority(Browser));
        var fourth = (await chat.StartAsync(new("Again", first.ConversationId), policy, Browser)).Turn;
        var purged = await chat.HistoryAsync(fourth.Id, Browser);
        Assert.DoesNotContain(purged, item => item.Text.Contains("Blue Otter", StringComparison.Ordinal));
        Assert.DoesNotContain(purged.SelectMany(item => item.DocumentReferences), id => id == document.Id);
    }

    [Fact]
    public async Task OversizedRecentHistoryIsCompactedRatherThanSilentlyOmitted()
    {
        using var root = new MigrationTestDataRoot(); await using var stores = new MigrationTestStores(root.Path);
        var chat = await SeedAsync(stores); var policy = Policy with { MaxHistoryBytes = 2048 };
        var first = (await chat.StartAsync(new(new string('x', 6000)), policy, Browser)).Turn;
        await chat.FinishAsync(first.Id, "complete", new(new string('y', 6000), []));
        var second = (await chat.StartAsync(new("Continue", first.ConversationId), policy, Browser)).Turn;
        var history = await chat.HistoryAsync(second.Id, Browser);
        Assert.Equal("system", Assert.Single(history).Role);
        Assert.Contains("omitted", history[0].Text);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(history[0].Text) <= 2048);
    }

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
