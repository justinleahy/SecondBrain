using System.Text.Json;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Chat;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed partial class ChatApprovalTests
{
    [Fact]
    public async Task PortableTranscriptKeepsInvocationOrderInsteadOfSortingOpaqueIds()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var turn = (await fixture.Chat.StartAsync(new("Question"), Policy, Browser)).Turn;
        var first = await fixture.Chat.BeginToolAsync(turn.Id, "search", JsonSerializer.SerializeToElement(new { query = "one" }), 0, Browser);
        await fixture.Chat.CompleteToolAsync(first, JsonSerializer.SerializeToElement(new { result = "first" }), [], Browser);
        var second = await fixture.Chat.BeginToolAsync(turn.Id, "query_records", JsonSerializer.SerializeToElement(new { type = "note" }), 0, Browser);
        await fixture.Chat.CompleteToolAsync(second, JsonSerializer.SerializeToElement(new { result = "second" }), [], Browser);
        await MigrationTestSql.WriteAsync(fixture.Stores.State, "UPDATE tool_executions SET id=CASE tool WHEN 'search' THEN 'z-first' ELSE 'a-second' END");
        var calls = Assert.Single(await fixture.Chat.TranscriptAsync(turn.ConversationId, Browser)).Calls;
        Assert.Equal(new[] { "search", "query_records" }, calls.Select(call => call.Name));
    }

    [Fact]
    public async Task RetryManifestKeepsEachProvidersOwnDocumentExposure()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var first = (await fixture.Documents.CaptureAsync(new("First source", "First"), Authority(Browser))).Document;
        var second = (await fixture.Documents.CaptureAsync(new("Second source", "Second"), Authority(Browser))).Document;
        var turn = (await fixture.Chat.StartAsync(new("Question"), Policy, Browser)).Turn;
        await fixture.Chat.ExposeAsync(turn.Id, [new(first.Id, 1, first.Chunks![0].Id)], Browser);
        await fixture.Chat.ReserveTokensAsync(turn.Id, "primary", 10, Browser);
        await fixture.Chat.RecordProviderExposureAsync(turn.Id, "primary", Browser);
        await fixture.Chat.FinishAsync(turn.Id, "partial", new("", []), "interrupted");
        await fixture.Chat.RetryAsync(turn.Id, new("switch"), Policy with { Provider = "fallback" }, Browser);
        await fixture.Chat.ExposeAsync(turn.Id, [new(second.Id, 1, second.Chunks![0].Id)], Browser);
        await fixture.Chat.ReserveTokensAsync(turn.Id, "secondary", 10, Browser);
        await fixture.Chat.RecordProviderExposureAsync(turn.Id, "secondary", Browser);
        var calls = (await fixture.Chat.ManifestAsync(turn.Id, Browser)).ProviderCalls!;
        Assert.Equal(new[] { "local", "fallback" }, calls.Select(call => call.Provider));
        Assert.Equal(first.Id, Assert.Single(calls[0].DocumentIds));
        Assert.Equal(new[] { first.Id, second.Id }.Order(), calls[1].DocumentIds.Order());
    }

    [Fact]
    public async Task PublicationBindsGeneratedDocumentLineageBeforeAssistantBookkeeping()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var turn = (await fixture.Chat.StartAsync(new("Save"), Policy, Browser)).Turn;
        var args = JsonSerializer.SerializeToElement(new { title = "Private", content = "COMMIT_PURGE_RACE_SECRET" });
        var call = await fixture.Chat.BeginToolAsync(turn.Id, "create_document", args, 0, Browser);
        var proposal = await fixture.Chat.ProposeForCallAsync(turn.Id, "create_document", args, call, Browser);
        await MigrationTestSql.WriteAsync(fixture.Stores.State, "UPDATE pending_operations SET status='executing',approved_by='browser'; UPDATE chat_proposal_details SET approver_generation=1,approver_epoch=1; UPDATE tool_executions SET status='running' WHERE status='pending_approval'");
        // Deliberately stop at the journaled effect, before ChatRepository's callback.
        await fixture.Documents.ExecuteApprovedAsync(new(proposal.Id, proposal.ExecutionId!, proposal.Plan!), Authority(Browser));
        await fixture.Documents.MutateAsync(proposal.Plan!.DocumentId, 1, "purge", JsonSerializer.SerializeToElement(new { }), Authority(Browser));
        var transcript = await fixture.Chat.TranscriptAsync(turn.ConversationId, Browser);
        Assert.True(Assert.Single(Assert.Single(transcript).Calls).Redacted);
        Assert.DoesNotContain("COMMIT_PURGE_RACE_SECRET", JsonSerializer.Serialize(transcript));
        Assert.Null(Assert.Single(await fixture.Chat.ListAsync(null, Browser)).Plan);
        await fixture.Chat.RecoverApprovalsAsync();
        Assert.Equal("executed", Assert.Single(await fixture.Chat.ListAsync(null, Browser)).Status);
        await fixture.Chat.FinishAsync(turn.Id, "partial", new("", []), "interrupted");
        await fixture.Chat.RetryAsync(turn.Id, new("resume-redacted"), Policy, Browser);
        Assert.True((await fixture.Chat.ResumeStateAsync(turn.Id, Browser)).HasCommittedWrite);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RetriedPurgeDoesNotResetExposureCapsOrReuseCitationNumbers(int maximumDocuments)
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var first = (await fixture.Documents.CaptureAsync(new("First", "First"), Authority(Browser))).Document;
        var second = (await fixture.Documents.CaptureAsync(new("Second", "Second"), Authority(Browser))).Document;
        var policy = Policy with { MaxReadDocuments = maximumDocuments };
        var turn = (await fixture.Chat.StartAsync(new("Question"), policy, Browser)).Turn;
        await fixture.Chat.ExposeAsync(turn.Id, [new(first.Id, 1, first.Chunks![0].Id)], Browser);
        await fixture.Chat.FinishAsync(turn.Id, "partial", new("", []), "interrupted");
        await fixture.Documents.MutateAsync(first.Id, 1, "purge", JsonSerializer.SerializeToElement(new { }), Authority(Browser));
        await fixture.Chat.RetryAsync(turn.Id, new("retry"), policy, Browser);
        if (maximumDocuments == 1)
            await Assert.ThrowsAsync<ChatLimitException>(() => fixture.Chat.ExposeAsync(turn.Id, [new(second.Id, 1, second.Chunks![0].Id)], Browser).AsTask());
        else
        {
            var sources = await fixture.Chat.ExposeAsync(turn.Id, [new(second.Id, 1, second.Chunks![0].Id)], Browser);
            Assert.Equal(2, Assert.Single(sources).Number);
        }
    }

    [Fact]
    public async Task RetryIsIdempotentAndRetainsBudgetsAndOriginalAuthority()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var started = await fixture.Chat.StartAsync(new("Retry this"), Policy, Key);
        await fixture.Chat.CountToolAsync(started.Turn.Id, 3, Key);
        await fixture.Chat.ReserveTokensAsync(started.Turn.Id, "spent", 100, Key);
        await fixture.Chat.SettleTokensAsync(started.Turn.Id, "spent", 70);
        await fixture.Chat.FinishAsync(started.Turn.Id, "partial", new("", []), "interrupted");
        var request = new ChatRetryRequest("retry-once");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Chat.RetryAsync(started.Turn.Id, request, Policy, Key).AsTask()));
        Assert.Single(results, result => result.Created);
        Assert.All(results, result => Assert.Equal(started.Turn.Id, result.Turn.Id));
        Assert.True(results[0].ReplayAfter > 0);
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT attempt FROM chat_turn_attempts"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT tool_calls FROM chat_turn_details"));
        Assert.Equal(70L, await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT charged_tokens FROM chat_model_calls"));
        await Assert.ThrowsAsync<ChatNotFoundException>(() => fixture.Chat.RetryAsync(started.Turn.Id, new("other"), Policy, Browser).AsTask());
        await Assert.ThrowsAsync<ChatConflictException>(() => fixture.Chat.RetryAsync(started.Turn.Id, request with { UseFallback = true }, Policy, Key).AsTask());
    }

    [Fact]
    public async Task CommittedWriteRejectsProviderSwitchAndIsPresentInPortableTranscript()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var turn = (await fixture.Chat.StartAsync(new("Save"), Policy, Browser)).Turn;
        var args = JsonSerializer.SerializeToElement(new { title = "Recorded", content = "One durable note" });
        var call = await fixture.Chat.BeginToolAsync(turn.Id, "create_document", args, 0, Browser);
        var proposal = await fixture.Chat.ProposeForCallAsync(turn.Id, "create_document", args, call, Browser);
        await fixture.Chat.ApproveAsync(proposal.Id, Browser);
        // Simulate failure after the effect but before its outer tool result was committed.
        await fixture.Chat.FailToolAsync(call, "failed");
        await fixture.Chat.FinishAsync(turn.Id, "failed", new("", []), "provider_or_tool_failed");
        await Assert.ThrowsAsync<ChatConflictException>(() => fixture.Chat.RetryAsync(turn.Id, new("switch"), Policy with { Provider = "other" }, Browser).AsTask());
        await fixture.Chat.RetryAsync(turn.Id, new("same"), Policy, Browser);
        var resumed = await fixture.Chat.ResumeStateAsync(turn.Id, Browser);
        Assert.True(resumed.HasCommittedWrite);
        var saved = Assert.Single(resumed.Calls);
        Assert.Equal("executed", saved.Status);
        Assert.Equal(proposal.Id, saved.Result!.Value.GetProperty("Id").GetString());
        var transcript = Assert.Single(await fixture.Chat.TranscriptAsync(turn.ConversationId, Browser));
        Assert.Equal(call, Assert.Single(transcript.Calls).Id);
        Assert.Equal("One durable note", await File.ReadAllTextAsync(proposal.Plan!.Destination));
    }

    [Fact]
    public async Task RetryAfterPurgeHasOnlyRedactionMarkersAndCanStillFinish()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var turn = (await fixture.Chat.StartAsync(new("Save"), Policy, Browser)).Turn;
        var args = JsonSerializer.SerializeToElement(new { title = "Secret", content = "NEVER_REINTRODUCE_SECRET" });
        var call = await fixture.Chat.BeginToolAsync(turn.Id, "create_document", args, 0, Browser);
        var proposal = await fixture.Chat.ProposeForCallAsync(turn.Id, "create_document", args, call, Browser);
        await fixture.Chat.ApproveAsync(proposal.Id, Browser);
        await fixture.Chat.FailToolAsync(call, "failed");
        await fixture.Chat.FinishAsync(turn.Id, "failed", new("", []), "interrupted");
        await fixture.Documents.MutateAsync(proposal.Plan!.DocumentId, 1, "purge", JsonSerializer.SerializeToElement(new { }), Authority(Browser));
        await fixture.Chat.RetryAsync(turn.Id, new("after-purge"), Policy, Browser);
        var state = await fixture.Chat.ResumeStateAsync(turn.Id, Browser);
        var entry = Assert.Single(state.Calls);
        Assert.True(entry.Redacted);
        Assert.DoesNotContain("NEVER_REINTRODUCE_SECRET", JsonSerializer.Serialize(state));
        Assert.Empty(entry.DocumentReferences);
        await fixture.Chat.CheckActiveAsync(turn.Id, Browser);
        await fixture.Chat.FinishAsync(turn.Id, "complete", new("Previous details were purged.", []));
        Assert.Equal("complete", (await fixture.Chat.GetTurnAsync(turn.Id, Browser)).Status);
    }

    [Fact]
    public async Task ANewPurgeAfterRetryStillStopsItsNextBoundary()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var source = (await fixture.Documents.CaptureAsync(new("Evidence", "Source"), Authority(Browser))).Document;
        var turn = (await fixture.Chat.StartAsync(new("Question"), Policy, Browser)).Turn;
        await fixture.Chat.ExposeAsync(turn.Id, [new(source.Id, 1, source.Chunks![0].Id)], Browser);
        await fixture.Chat.FinishAsync(turn.Id, "partial", new("", []), "interrupted");
        await fixture.Chat.RetryAsync(turn.Id, new("resume"), Policy, Browser);
        await fixture.Documents.MutateAsync(source.Id, 1, "purge", JsonSerializer.SerializeToElement(new { }), Authority(Browser));
        await Assert.ThrowsAsync<ChatConflictException>(() => fixture.Chat.CheckActiveAsync(turn.Id, Browser).AsTask());
    }
}
