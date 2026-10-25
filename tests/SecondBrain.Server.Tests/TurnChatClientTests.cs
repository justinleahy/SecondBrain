using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Providers;
using SecondBrain.Server.Chat;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class TurnChatClientTests
{
    [Fact]
    public async Task NativeEvidenceIsSentOnceAndToolResultMetadataDoesNotDuplicateItsText()
    {
        var source=Source("Captured source text");var repository=new Repository {Sources=[source]};
        var provider=new Provider(_=>new(new ChatMessage(ChatRole.Assistant,"Answer")));
        var citations=new NativeCitationAnswer();using var client=new TurnChatClient(provider,repository,"turn",Actor,Policy,10000,()=>true,citations);
        await client.GetResponseAsync([new(ChatRole.Tool,[new FunctionResultContent("call",new[]{source})])],new() {MaxOutputTokens=128});
        var document=Assert.Single(provider.Messages!.SelectMany(message=>message.Contents).OfType<CitationSourceContent>());Assert.Equal(source,document.Source);
        var tool=Assert.Single(provider.Messages!.SelectMany(message=>message.Contents).OfType<FunctionResultContent>());
        Assert.DoesNotContain(source.Excerpt,JsonSerializer.Serialize(tool.Result));Assert.Contains("document",JsonSerializer.Serialize(tool.Result));
        citations.Append(new CitationSpanContent(1,0,8,"Captured"));Assert.Single(citations.Verify().Citations);
    }
    [Fact]
    public async Task NativeDocumentBytesAreBudgetedBeforeTheProviderCanSeeThem()
    {
        var repository=new Repository {Sources=[Source(new string('x',6000))]};var provider=new Provider(_=>new(new ChatMessage(ChatRole.Assistant,"unused")));
        using var client=new TurnChatClient(provider,repository,"turn",Actor,Policy,5000,()=>true,new NativeCitationAnswer());
        await Assert.ThrowsAsync<ChatLimitException>(()=>client.GetResponseAsync([new(ChatRole.User,"question")],new() {MaxOutputTokens=128}));
        Assert.Equal(0,provider.Calls);Assert.Empty(repository.Reservations);
    }
    [Fact]
    public async Task NativeEvidenceHashChangesFailBeforeAProviderRequest()
    {
        var repository=new Repository {Sources=[Source("Original") with {Excerpt="Tampered"}]};var provider=new Provider(_=>new(new ChatMessage(ChatRole.Assistant,"unused")));
        using var client=new TurnChatClient(provider,repository,"turn",Actor,Policy,5000,()=>true,new NativeCitationAnswer());
        await Assert.ThrowsAsync<ChatConflictException>(()=>client.GetResponseAsync([new(ChatRole.User,"question")],new() {MaxOutputTokens=128}));
        Assert.Equal(0,provider.Calls);
    }
    private static ChatSource Source(string text)=>new(1,"document",1,"generation","chunk",0,null,"Fixture",text,Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingUsageIsSummedOnlyWhenTheCallCompletes(bool interrupted)
    {
        var repository = new Repository();
        var provider = new Provider(_ => new(new ChatMessage(ChatRole.Assistant, "unused"))) { EmitUsage = true, FailAfterUsage = interrupted };
        using var client = new TurnChatClient(provider, repository, "turn", Actor, Policy, 5000, () => true);
        async Task Read() { await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "Hi")], new() { MaxOutputTokens = 128 })) { } }
        if (interrupted) await Assert.ThrowsAsync<HttpRequestException>(Read);
        else await Read();
        if (interrupted) Assert.Null(Assert.Single(repository.Settlements));
        else
        {
            Assert.Equal(15, Assert.Single(repository.Settlements));
            Assert.Equal(10, repository.Detailed!.InputTokens); Assert.Equal(4, repository.Detailed.CachedInputTokens);
        }
    }

    [Fact]
    public async Task ReasoningContinuationIsBudgetedAndCannotChangeTheFrozenPreference()
    {
        var repository = new Repository();
        var provider = new Provider(_ => new(new ChatMessage(ChatRole.Assistant, "Final")));
        using var client = new TurnChatClient(provider, repository, "turn", Actor, Policy with { Reasoning = "high" }, 5000, () => true);
        var messages = new[] { new ChatMessage(ChatRole.Assistant, [new TextReasoningContent(new string('x', 500))]) };
        var options = new ChatOptions { MaxOutputTokens = 128, Reasoning = new() { Effort = ReasoningEffort.High } };
        await client.GetResponseAsync(messages, options);
        Assert.True(Assert.Single(repository.Reservations) >= 1652);
        await Assert.ThrowsAsync<ChatLimitException>(() => client.GetResponseAsync(messages,
            new ChatOptions { MaxOutputTokens = 128, Reasoning = new() { Effort = ReasoningEffort.Low } }));
    }

    private static readonly AuthenticatedCredential Actor = new("test", "session", 1, 1, new HashSet<Scope> { Scope.Read, Scope.Infer }, null);
    private static readonly ChatTurnPolicy Policy = new("test", "test", MaxOutputTokens: 128);

    [Fact]
    public async Task FunctionContinuationGetsItsOwnBudgetAndAuthorityBoundary()
    {
        var repository = new Repository();
        var provider = new Provider(call => new ChatResponse(new ChatMessage(ChatRole.Assistant, call == 1
            ? [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>())]
            : [new TextContent("The answer is 42.")])) { Usage = new UsageDetails { TotalTokenCount = 50 } });
        using var client = new FunctionInvokingChatClient(new TurnChatClient(provider, repository, "turn", Actor, Policy, 20_000, () => true));
        var result = await client.GetResponseAsync([new(ChatRole.User, "What is the answer?")],
            new ChatOptions { MaxOutputTokens = 128, Tools = [AIFunctionFactory.Create(() => "42", "lookup")] });
        Assert.Equal("The answer is 42.", result.Text);
        Assert.Equal(2, repository.Reservations.Count);
        Assert.Equal(2, repository.Settlements.Count);
        Assert.All(repository.Settlements, item => Assert.Equal(50, item));
        Assert.Equal(2, repository.Checks);
        client.Dispose();
        Assert.False(provider.Disposed);
    }

    [Fact]
    public async Task ChangedConfigurationOrOversizedPromptCannotReachProvider()
    {
        var repository = new Repository(); var provider = new Provider(_ => new(new ChatMessage(ChatRole.Assistant, "Unexpected")));
        var changed = new TurnChatClient(provider, repository, "turn", Actor, Policy, 5000, () => false);
        await Assert.ThrowsAsync<ChatConflictException>(() => changed.GetResponseAsync([new(ChatRole.User, "Hi")], new() { MaxOutputTokens = 128 }));
        var bounded = new TurnChatClient(provider, repository, "turn", Actor, Policy, 5000, () => true);
        await Assert.ThrowsAsync<ChatLimitException>(() => bounded.GetResponseAsync([new(ChatRole.User, new string('x', 6000))], new() { MaxOutputTokens = 128 }));
        await Assert.ThrowsAsync<ChatLimitException>(() => bounded.GetResponseAsync([new(ChatRole.User, "Hi")], new() { MaxOutputTokens = 129 }));
        Assert.Equal(0, provider.Calls); Assert.Empty(repository.Reservations);
    }

    [Fact]
    public async Task RevocationDuringStreamingStopsDeliveryAndUnknownUsageKeepsReservation()
    {
        var repository = new Repository { RevokeOnCheck = 2 };
        var provider = new Provider(_ => new(new ChatMessage(ChatRole.Assistant, "unused")));
        using var client = new TurnChatClient(provider, repository, "turn", Actor, Policy, 5000, () => true);
        List<string> delivered = [];
        await Assert.ThrowsAsync<AuthorityChangedException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "Hi")], new() { MaxOutputTokens = 128 }))
                delivered.Add(update.Text);
        });
        Assert.Equal(new[] { "first" }, delivered);
        Assert.Null(Assert.Single(repository.Settlements));
    }

    private sealed class Provider(Func<int, ChatResponse> response) : IChatClient
    {
        public bool EmitUsage { get; init; }
        public bool FailAfterUsage { get; init; }
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public ChatMessage[]? Messages;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) {Messages=messages.ToArray();return Task.FromResult(response(++Calls));}
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            yield return new(ChatRole.Assistant, "first");
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(ChatRole.Assistant, "second");
            if (EmitUsage)
            {
                yield return new ChatResponseUpdate { Contents = [new UsageContent(new UsageDetails { InputTokenCount = 8, OutputTokenCount = 2, CachedInputTokenCount = 3, TotalTokenCount = 10 })] };
                yield return new ChatResponseUpdate { Contents = [new UsageContent(new UsageDetails { InputTokenCount = 2, OutputTokenCount = 3, CachedInputTokenCount = 1, TotalTokenCount = 5 })] };
                if (FailAfterUsage) throw new HttpRequestException("interrupted");
            }
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => Disposed = true;
    }

    private sealed class Repository : IChatRepository
    {
        public IReadOnlyList<ChatSource> Sources {get;init;}=[];
        public List<long> Reservations { get; } = [];
        public List<long?> Settlements { get; } = [];
        public ModelUsageTotals? Detailed { get; private set; }
        public ValueTask SettleUsageAsync(string turnId, string callId, ModelUsageTotals? totals, CancellationToken cancellationToken = default)
        { Detailed = totals; return SettleTokensAsync(turnId, callId, totals?.TotalTokens, cancellationToken); }
        public int Checks { get; private set; }
        public int RevokeOnCheck { get; init; } = int.MaxValue;
        public ValueTask CheckActiveAsync(string turnId, AuthenticatedCredential actor, CancellationToken cancellationToken = default)
        {
            if (++Checks >= RevokeOnCheck) throw new AuthorityChangedException();
            return ValueTask.CompletedTask;
        }
        public ValueTask ReserveTokensAsync(string turnId, string callId, long tokens, AuthenticatedCredential actor, CancellationToken cancellationToken = default) { Reservations.Add(tokens); return ValueTask.CompletedTask; }
        public ValueTask SettleTokensAsync(string turnId, string callId, long? actualTokens, CancellationToken cancellationToken = default) { Settlements.Add(actualTokens); return ValueTask.CompletedTask; }
        public ValueTask<ChatStart> StartAsync(ChatRequest request, ChatTurnPolicy policy, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ChatConversation>> ListAsync(AuthenticatedCredential actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DeleteConversationAsync(string id, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ChatTurn>> GetConversationAsync(string id, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ChatTurn> GetTurnAsync(string id, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ChatHistoryMessage>> HistoryAsync(string turnId, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ChatEvent>> EventsAsync(string turnId, long after, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<long> AppendAsync(string turnId, string kind, JsonElement data, IReadOnlyList<string> documentReferences, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ChatSource>> ExposeAsync(string turnId, IReadOnlyList<ChatSourceRequest> sources, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ChatSource>> SourcesAsync(string turnId, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => ValueTask.FromResult(Sources);
        public ValueTask CountToolAsync(string turnId, int metadataRecords, AuthenticatedCredential actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask FinishAsync(string turnId, string status, ChatAnswer answer, string? failureCode = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
