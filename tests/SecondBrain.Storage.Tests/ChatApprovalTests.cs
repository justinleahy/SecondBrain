using System.Text.Json;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Durability;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Retrieval;
using SecondBrain.Infrastructure.Ingestion;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage.Chat;
using SecondBrain.Storage.Durability;
using SecondBrain.Storage.Ingestion;
using SecondBrain.Storage.Retrieval;
using Xunit;

namespace SecondBrain.Storage.Tests;

public sealed class ChatApprovalTests
{
    private static readonly AuthenticatedCredential Browser = Actor("browser", "session");
    private static readonly AuthenticatedCredential Key = Actor("key", "api_key");
    private static readonly ChatTurnPolicy Policy = new("local", "test-model");

    [Fact]
    public async Task NextTurnReceivesTheConfirmedApprovalOutcome()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var turn = (await fixture.Chat.StartAsync(new("Save"), Policy, Browser)).Turn;
        var proposal = await fixture.Chat.ProposeAsync(turn.Id, "create_document", JsonSerializer.SerializeToElement(new { title = "Note", content = "Saved" }), Browser);
        await fixture.Chat.FinishAsync(turn.Id, "complete", new("Waiting for approval.", []));
        await fixture.Chat.ApproveAsync(proposal.Id, Browser);
        var next = (await fixture.Chat.StartAsync(new("Was that saved?", turn.ConversationId), Policy, Browser)).Turn;
        var history = await fixture.Chat.HistoryAsync(next.Id, Browser);
        var receipt = Assert.Single(history, message => message.Text.Contains("Recorded operation status", StringComparison.Ordinal));
        Assert.Contains("executed", receipt.Text);
        Assert.Contains(proposal.Plan!.DocumentId, receipt.DocumentReferences);
    }

    [Fact]
    public async Task ConcurrentApprovalsCreateOneNoteAndOneExecution()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var turn = (await fixture.Chat.StartAsync(new("Save this"), Policy, Browser)).Turn;
        var proposal = await fixture.Chat.ProposeAsync(turn.Id, "create_document", JsonSerializer.SerializeToElement(new { title = "Note", content = "Blue Otter" }), Browser);
        Assert.False(File.Exists(proposal.Plan!.Destination));
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => fixture.Chat.ApproveAsync(proposal.Id, Browser).AsTask()));
        Assert.Contains(results, result => result.Status == "executed");
        Assert.Equal("Blue Otter", await File.ReadAllTextAsync(proposal.Plan.Destination));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(root.Path, "vault", "notes")));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT COUNT(*) FROM documents"));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT COUNT(*) FROM tool_executions WHERE status='committed'"));
        Assert.Equal("executed", (await fixture.Chat.ApproveAsync(proposal.Id, Browser)).Status);
    }

    [Theory]
    [InlineData(MutationCrashPointNames.AfterTempFlush)]
    [InlineData(CrashPointNames.BeforeFinalize)]
    [InlineData(CrashPointNames.AfterStatePublishing)]
    public async Task CreateRecoversItsStableIdentityAcrossFileAndPublicationCrashes(string point)
    {
        using var root = new MigrationTestDataRoot(); ChatProposal proposal;
        await using (var fixture = await Fixture.CreateAsync(root.Path, new CrashOnce(point)))
        {
            var turn = (await fixture.Chat.StartAsync(new("Save once"), Policy, Browser)).Turn;
            proposal = await fixture.Chat.ProposeAsync(turn.Id, "create_document", JsonSerializer.SerializeToElement(new { title = "Note", content = "Only once" }), Browser);
            Assert.Equal("executing", (await fixture.Chat.ApproveAsync(proposal.Id, Browser)).Status);
        }
        await using (var recovered = await Fixture.CreateAsync(root.Path))
        {
            await recovered.Journal.RecoverAsync();
            await recovered.Coordinator.RecoverAsync();
            await recovered.Chat.RecoverApprovalsAsync();
            var completed = Assert.Single(await recovered.Chat.ListAsync(null, Browser));
            Assert.Equal("executed", completed.Status);
            Assert.Equal(proposal.Plan!.DocumentId, completed.Plan!.DocumentId);
            Assert.Equal("Only once", await File.ReadAllTextAsync(completed.Plan.Destination));
            Assert.Equal(1L, await MigrationTestSql.ScalarAsync(recovered.Stores.State, "SELECT COUNT(*) FROM documents"));
            Assert.Equal(1L, await MigrationTestSql.ScalarAsync(recovered.Stores.State, "SELECT COUNT(*) FROM mutations"));
            Assert.Equal(0, await recovered.Chat.RecoverApprovalsAsync());
        }
    }

    [Fact]
    public async Task UpdateCrashFinalizesTheOriginalRevisionWithoutApplyingItTwice()
    {
        using var root = new MigrationTestDataRoot(); ChatProposal proposal;
        await using (var setup = await Fixture.CreateAsync(root.Path))
        {
            var document = (await setup.Documents.CaptureAsync(new("Before", "Note"), Authority(Browser))).Document;
            var turn = (await setup.Chat.StartAsync(new("Update " + document.Id), Policy, Browser)).Turn;
            proposal = await setup.Chat.ProposeAsync(turn.Id, "update_document", JsonSerializer.SerializeToElement(new { id = document.Id, revision = 1, content = "After" }), Browser);
        }
        await using (var crashing = await Fixture.CreateAsync(root.Path, new CrashOnce(CrashPointNames.BeforeFinalize)))
            Assert.Equal("executing", (await crashing.Chat.ApproveAsync(proposal.Id, Browser)).Status);
        await using (var recovered = await Fixture.CreateAsync(root.Path))
        {
            await recovered.Journal.RecoverAsync(); await recovered.Coordinator.RecoverAsync(); await recovered.Chat.RecoverApprovalsAsync();
            Assert.Equal("executed", (await recovered.Chat.ApproveAsync(proposal.Id, Browser)).Status);
            var document = await recovered.Documents.GetAsync(proposal.Plan!.DocumentId, true);
            Assert.Equal(2, document!.Revision); Assert.Equal("After", document.Text!.TrimEnd());
            Assert.Equal("After", await File.ReadAllTextAsync(document.Path!));
        }
    }

    [Fact]
    public async Task KeyMayProposeButOnlyAnInteractiveSessionCanApproveOrDiscard()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var turn = (await fixture.Chat.StartAsync(new("Save this"), Policy, Key)).Turn;
        var proposal = await fixture.Chat.ProposeAsync(turn.Id, "create_document", JsonSerializer.SerializeToElement(new { title = "From key", content = "Pending" }), Key);
        await Assert.ThrowsAsync<AuthorityChangedException>(() => fixture.Chat.ApproveAsync(proposal.Id, Key).AsTask());
        await Assert.ThrowsAsync<AuthorityChangedException>(() => fixture.Chat.DiscardAsync(proposal.Id, Key).AsTask());
        Assert.Single(await fixture.Chat.ListAsync(null, Browser));
        Assert.Equal("executed", (await fixture.Chat.ApproveAsync(proposal.Id, Browser)).Status);
    }

    [Fact]
    public async Task AskAndNarrowedCredentialsCannotProposeWrites()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var ask = (await fixture.Chat.StartAsync(new("Save", Mode: "ask"), Policy, Browser)).Turn;
        var args = JsonSerializer.SerializeToElement(new { title = "Note", content = "No write" });
        await Assert.ThrowsAsync<AuthorityChangedException>(() => fixture.Chat.ProposeAsync(ask.Id, "create_document", args, Browser).AsTask());
        var chat = (await fixture.Chat.StartAsync(new("Save"), Policy, Browser)).Turn;
        await MigrationTestSql.WriteAsync(fixture.Stores.State, "UPDATE credentials SET scopes='read,infer' WHERE id='browser'");
        await Assert.ThrowsAsync<AuthorityChangedException>(() => fixture.Chat.ProposeAsync(chat.Id, "create_document", args, Browser).AsTask());
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT COUNT(*) FROM pending_operations"));
    }

    [Fact]
    public async Task ChangedTargetsAndChangedPolicyFailWithoutWriting()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var document = (await fixture.Documents.CaptureAsync(new("Before", "Note"), Authority(Browser))).Document;
        var turn = (await fixture.Chat.StartAsync(new("Update " + document.Id), Policy, Browser)).Turn;
        var proposal = await fixture.Chat.ProposeAsync(turn.Id, "update_document", JsonSerializer.SerializeToElement(new { id = document.Id, revision = 1, content = "Proposal" }), Browser);
        await fixture.Documents.MutateAsync(document.Id, 1, "update", JsonSerializer.SerializeToElement(new { content = "External edit" }), Authority(Browser));
        Assert.Equal("target_changed", (await fixture.Chat.ApproveAsync(proposal.Id, Browser)).Status);
        Assert.Equal("External edit", await File.ReadAllTextAsync(document.Path!));
        var another = await fixture.Chat.ProposeAsync(turn.Id, "create_document", JsonSerializer.SerializeToElement(new { title = "Note", content = "No write" }), Browser);
        fixture.Options.CurrentValue.Assistant.PendingTtlHours++;
        Assert.Equal("policy_changed", (await fixture.Chat.ApproveAsync(another.Id, Browser)).Status);
        Assert.False(File.Exists(another.Plan!.Destination));
    }

    [Fact]
    public async Task EditSupersedesWithoutChangingTheOriginalPayloadAndDiscardHasNoEffect()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var turn = (await fixture.Chat.StartAsync(new("Save"), Policy, Browser)).Turn;
        var first = await fixture.Chat.ProposeAsync(turn.Id, "create_document", JsonSerializer.SerializeToElement(new { title = "Note", content = "Original" }), Browser);
        var edited = await fixture.Chat.EditAsync(first.Id, JsonSerializer.SerializeToElement(new { title = "Note", content = "Edited" }), Browser);
        Assert.NotEqual(first.Id, edited.Id);
        Assert.Equal("superseded", (await fixture.Chat.ApproveAsync(first.Id, Browser)).Status);
        var all = await fixture.Chat.ListAsync(turn.ConversationId, Browser);
        Assert.Equal("Original", all.Single(item => item.Id == first.Id).Plan!.Payload.GetProperty("content").GetString());
        Assert.Equal("discarded", (await fixture.Chat.DiscardAsync(edited.Id, Browser)).Status);
        Assert.Equal("discarded", (await fixture.Chat.ApproveAsync(edited.Id, Browser)).Status);
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(fixture.Stores.State, "SELECT COUNT(*) FROM documents"));
    }

    [Fact]
    public async Task AutoApprovalRequiresBrowserNoteScopeAndNoRetrievedContext()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        fixture.Options.CurrentValue.Assistant.AutoApproveWrites = true;
        var policy = Policy with { AutoApproveNotes = true };
        var turn = (await fixture.Chat.StartAsync(new("Save"), policy, Browser)).Turn;
        var first = await fixture.Chat.ProposeAsync(turn.Id, "create_document", JsonSerializer.SerializeToElement(new { title = "Note", content = "Allowed" }), Browser);
        Assert.Equal("executed", (await fixture.Chat.ApproveAsync(first.Id, Browser, automatic: true)).Status);
        var document = (await fixture.Documents.GetAsync(first.Plan!.DocumentId, true))!;
        var next = (await fixture.Chat.StartAsync(new("Save after reading"), policy, Browser)).Turn;
        await fixture.Chat.ExposeAsync(next.Id, [new(document.Id, 1, document.Chunks![0].Id)], Browser);
        var refused = await fixture.Chat.ProposeAsync(next.Id, "create_document", JsonSerializer.SerializeToElement(new { title = "Note", content = "Needs a person" }), Browser);
        await Assert.ThrowsAsync<AuthorityChangedException>(() => fixture.Chat.ApproveAsync(refused.Id, Browser, automatic: true).AsTask());
        Assert.False(File.Exists(refused.Plan!.Destination));
        Assert.Equal("executed", (await fixture.Chat.ApproveAsync(refused.Id, Browser)).Status);
    }

    [Fact]
    public async Task AutoAppendKeepsTheManagedNoteAndCannotChangeMetadata()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        fixture.Options.CurrentValue.Assistant.AutoApproveWrites = true;
        var policy = Policy with { AutoApproveNotes = true };
        var firstTurn = (await fixture.Chat.StartAsync(new("Save"), policy, Browser)).Turn;
        var created = await fixture.Chat.ProposeAsync(firstTurn.Id, "create_document", JsonSerializer.SerializeToElement(new { title = "Note", content = "First" }), Browser);
        await fixture.Chat.ApproveAsync(created.Id, Browser, automatic: true);
        var turn = (await fixture.Chat.StartAsync(new("Append to " + created.Plan!.DocumentId), policy, Browser)).Turn;
        var append = await fixture.Chat.ProposeAsync(turn.Id, "update_document", JsonSerializer.SerializeToElement(new { id = created.Plan.DocumentId, revision = 1, content = "Second", append = true }), Browser);
        Assert.Equal("executed", (await fixture.Chat.ApproveAsync(append.Id, Browser, automatic: true)).Status);
        Assert.Equal("First\nSecond", (await File.ReadAllTextAsync(created.Plan.Destination)).ReplaceLineEndings("\n").TrimEnd());
        Assert.Equal(created.Plan.Destination, (await fixture.Documents.GetAsync(created.Plan.DocumentId, true))!.Path);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Chat.ProposeAsync(turn.Id, "update_document", JsonSerializer.SerializeToElement(new { id = created.Plan.DocumentId, revision = 2, content = "Third", append = true, title = "Changed" }), Browser).AsTask());
    }

    [Fact]
    public async Task LinkProposalBindsBothDocumentsAndPublishesAnIdBasedLink()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var first = (await fixture.Documents.CaptureAsync(new("First", "Duplicate title"), Authority(Browser))).Document;
        var second = (await fixture.Documents.CaptureAsync(new("Second", "Duplicate title"), Authority(Browser))).Document;
        var turn = (await fixture.Chat.StartAsync(new("Link " + first.Id + " to " + second.Id), Policy, Browser)).Turn;
        var proposal = await fixture.Chat.ProposeAsync(turn.Id, "link_documents", JsonSerializer.SerializeToElement(new { id = first.Id, revision = 1, targetId = second.Id }), Browser);
        Assert.Equal("executed", (await fixture.Chat.ApproveAsync(proposal.Id, Browser)).Status);
        Assert.Contains("[[" + second.Id + "]]", await File.ReadAllTextAsync(first.Path!));
        Assert.Equal(second.Id, await MigrationTestSql.ScalarAsync(fixture.Stores.Index, "SELECT to_document_id FROM links"));
        var retrieval = new SqliteRetrievalRepository(fixture.Stores.Index, new UnusedProtection(), state: fixture.Stores.State);
        Assert.Equal(second.Id, Assert.Single(await retrieval.RelatedAsync(first.Id, Authority(Browser))).Id);
        Assert.Equal(first.Id, Assert.Single(await retrieval.RelatedAsync(second.Id, Authority(Browser))).Id);
    }

    [Fact]
    public async Task TagProposalPreservesTheOriginalBytes()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        const string original = "First line\r\nSecond line without final newline";
        var document = (await fixture.Documents.CaptureAsync(new(original, "Note"), Authority(Browser))).Document;
        var turn = (await fixture.Chat.StartAsync(new("Tag " + document.Id), Policy, Browser)).Turn;
        var proposal = await fixture.Chat.ProposeAsync(turn.Id, "tag_document", JsonSerializer.SerializeToElement(new { id = document.Id, revision = 1, tags = new[] { "project" } }), Browser);
        Assert.Equal("executed", (await fixture.Chat.ApproveAsync(proposal.Id, Browser)).Status);
        Assert.Equal(original, await File.ReadAllTextAsync(document.Path!));
        Assert.Contains("project", (await fixture.Documents.GetAsync(document.Id, true))!.Tags!);
    }

    [Fact]
    public async Task ExpiredOrPurgedProposalCannotProduceAFileWrite()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var document = (await fixture.Documents.CaptureAsync(new("Secret", "Note"), Authority(Browser))).Document;
        var turn = (await fixture.Chat.StartAsync(new("Update " + document.Id), Policy, Browser)).Turn;
        var proposal = await fixture.Chat.ProposeAsync(turn.Id, "update_document", JsonSerializer.SerializeToElement(new { id = document.Id, revision = 1, content = "Changed" }), Browser);
        await fixture.Documents.MutateAsync(document.Id, 1, "purge", JsonSerializer.SerializeToElement(new { }), Authority(Browser));
        var purged = await fixture.Chat.ApproveAsync(proposal.Id, Browser);
        Assert.Equal("source_purged", purged.Status); Assert.Null(purged.Plan);
        var expired = await fixture.Chat.ProposeAsync((await fixture.Chat.StartAsync(new("Save"), Policy, Browser)).Turn.Id, "create_document", JsonSerializer.SerializeToElement(new { title = "Note", content = "Too late" }), Browser);
        await MigrationTestSql.WriteAsync(fixture.Stores.State, "UPDATE pending_operations SET expires_at='2000-01-01T00:00:00Z' WHERE status='pending'");
        Assert.Equal("expired", (await fixture.Chat.ApproveAsync(expired.Id, Browser)).Status);
        Assert.False(File.Exists(expired.Plan!.Destination));
    }

    [Fact]
    public async Task RevokedCreatorAndTamperedPayloadCannotExecute()
    {
        using var root = new MigrationTestDataRoot(); await using var fixture = await Fixture.CreateAsync(root.Path);
        var turn = (await fixture.Chat.StartAsync(new("Save"), Policy, Key)).Turn;
        var proposal = await fixture.Chat.ProposeAsync(turn.Id, "create_document", JsonSerializer.SerializeToElement(new { title = "Note", content = "No write" }), Key);
        await MigrationTestSql.WriteAsync(fixture.Stores.State, "UPDATE credentials SET generation=2 WHERE id='key'");
        Assert.Equal("creator_changed", (await fixture.Chat.ApproveAsync(proposal.Id, Browser)).Status);
        Assert.False(File.Exists(proposal.Plan!.Destination));
        var anotherTurn = (await fixture.Chat.StartAsync(new("Save"), Policy, Browser)).Turn;
        var another = await fixture.Chat.ProposeAsync(anotherTurn.Id, "create_document", JsonSerializer.SerializeToElement(new { title = "Note", content = "Untampered" }), Browser);
        await MigrationTestSql.WriteAsync(fixture.Stores.State, "UPDATE pending_operations SET payload_hash='invalid' WHERE status='pending'");
        Assert.Equal("payload_changed", (await fixture.Chat.ApproveAsync(another.Id, Browser)).Status);
        Assert.False(File.Exists(another.Plan!.Destination));
    }

    private static AuthenticatedCredential Actor(string id, string kind) => new(id, kind, 1, 1, new HashSet<Scope> { Scope.Read, Scope.Write, Scope.Infer, Scope.Admin }, null);
    private static IngestionAuthority Authority(AuthenticatedCredential actor) => new(actor.Id, actor.Generation, actor.AccountEpoch, actor.Id, actor.Kind);

    private sealed class Fixture : IAsyncDisposable
    {
        public required MigrationTestStores Stores { get; init; }
        public required MutationJournal Journal { get; init; }
        public required ManagedDocumentFiles Files { get; init; }
        public required PublicationCoordinator Coordinator { get; init; }
        public required SqliteDocumentRepository Documents { get; init; }
        public required SqliteChatRepository Chat { get; init; }
        public required Monitor Options { get; init; }
        public static async Task<Fixture> CreateAsync(string root, ICrashPoints? crash = null)
        {
            var stores = new MigrationTestStores(root); await stores.Runner.MigrateAsync();
            await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key,value) VALUES('account_epoch','1') ON CONFLICT(key) DO NOTHING");
            var auth = new AuthRepository(stores.State, TimeProvider.System);
            foreach (var actor in new[] { Browser, Key })
                if (await auth.FindAsync(actor.Id) is null)
                    await auth.AddAsync(new CredentialRecord { Id = actor.Id, Name = actor.Id, Kind = actor.Kind, Generation = 1, AccountEpoch = 1,
                        Scopes = "read,write,infer,admin", Kid = "fixture", Verifier = "fixture", CreatedAt = AuthTime.Format(DateTimeOffset.UtcNow), SteppedUpAt = AuthTime.Format(DateTimeOffset.UtcNow) });
            var journal = new MutationJournal(stores.State, root, crash);
            var files = new ManagedDocumentFiles(root, journal);
            var coordinator = new PublicationCoordinator(stores.State, stores.Index, crash);
            var options = new Monitor(new SecondBrainOptions { DataRoot = root });
            var documents = new SqliteDocumentRepository(stores.State, stores.Index, coordinator, files, Microsoft.Extensions.Options.Options.Create(options.CurrentValue));
            return new() { Stores = stores, Journal = journal, Files = files, Coordinator = coordinator, Documents = documents, Options = options,
                Chat = new(stores.State, TimeProvider.System, documents, files, options) };
        }
        public async ValueTask DisposeAsync() { Files.Dispose(); Journal.Dispose(); await Stores.DisposeAsync(); }
    }
    private sealed class Monitor(SecondBrainOptions value) : IOptionsMonitor<SecondBrainOptions>
    {
        public SecondBrainOptions CurrentValue => value;
        public SecondBrainOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<SecondBrainOptions, string?> listener) => null;
    }
    private sealed class CrashOnce(string point) : ICrashPoints
    {
        private int crashed;
        public ValueTask HitAsync(string observed, CancellationToken cancellationToken = default)
        {
            if (observed == point && Interlocked.Exchange(ref crashed, 1) == 0) throw new InjectedFailure();
            return ValueTask.CompletedTask;
        }
    }
    private sealed class InjectedFailure : Exception;
    private sealed class UnusedProtection : IPayloadProtection
    {
        public string Protect(string purpose, string plaintext) => throw new NotSupportedException();
        public string Unprotect(string purpose, string protectedText) => throw new NotSupportedException();
    }
}
