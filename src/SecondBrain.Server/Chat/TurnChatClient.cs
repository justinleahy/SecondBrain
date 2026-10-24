using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Chat;

namespace SecondBrain.Server.Chat;

/// <summary>Every provider round trip, including a function-loop continuation, revalidates its initiator and reserves tokens.</summary>
public sealed class TurnChatClient(IChatClient inner, IChatRepository repository, string turnId,
    AuthenticatedCredential actor, ChatTurnPolicy policy, int contextTokens, Func<bool> configurationCurrent) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var prompt = messages.ToList();
        var id = await ReserveAsync(prompt, options, cancellationToken);
        long? usage = null;
        try
        {
            var response = await InnerClient.GetResponseAsync(prompt, options, cancellationToken);
            usage = response.Usage?.TotalTokenCount;
            await repository.CheckActiveAsync(turnId, actor, cancellationToken);
            return response;
        }
        finally { await repository.SettleTokensAsync(turnId, id, usage, CancellationToken.None); }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = messages.ToList();
        var id = await ReserveAsync(prompt, options, cancellationToken);
        long? usage = null;
        try
        {
            await foreach (var update in InnerClient.GetStreamingResponseAsync(prompt, options, cancellationToken))
            {
                foreach (var detail in update.Contents.OfType<UsageContent>())
                    if (detail.Details.TotalTokenCount is { } count) usage = Math.Max(usage ?? 0, count);
                await repository.CheckActiveAsync(turnId, actor, cancellationToken);
                yield return update;
            }
        }
        finally { await repository.SettleTokensAsync(turnId, id, usage, CancellationToken.None); }
    }

    private async ValueTask<string> ReserveAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options, CancellationToken token)
    {
        if (!configurationCurrent()) throw new ChatConflictException("The provider configuration changed during this turn.");
        if (options?.MaxOutputTokens is not { } output || output < 1 || output > policy.MaxOutputTokens || options.RawRepresentationFactory is not null ||
            options.ModelId is not null && options.ModelId != policy.Model)
            throw new ChatLimitException("The provider request does not match the frozen turn limits.");
        ReasoningEffort? reasoning = policy.Reasoning switch
        {
            "low" => ReasoningEffort.Low, "medium" => ReasoningEffort.Medium, "high" => ReasoningEffort.High, _ => null
        };
        if (options.Reasoning?.Effort != reasoning) throw new ChatLimitException("The reasoning preference changed during this turn.");
        long bytes = 1024;
        foreach (var message in messages)
        {
            bytes += 64;
            foreach (var content in message.Contents)
            {
                bytes += content switch
                {
                    TextContent text => Encoding.UTF8.GetByteCount(text.Text),
                    TextReasoningContent thought => JsonSerializer.SerializeToUtf8Bytes(new { thought.Text, thought.ProtectedData }).LongLength,
                    FunctionCallContent call => Encoding.UTF8.GetByteCount(call.Name) + JsonSerializer.SerializeToUtf8Bytes(call.Arguments).LongLength + 64,
                    FunctionResultContent result => JsonSerializer.SerializeToUtf8Bytes(result.Result).LongLength + 64,
                    _ => throw new ChatLimitException("This chat supports text and registered tool results only.")
                };
            }
        }
        foreach (var tool in options.Tools ?? [])
        {
            if (tool is not AIFunction function) throw new ChatLimitException("Only registered functions can be exposed to the model.");
            bytes += Encoding.UTF8.GetByteCount(function.Name + function.Description + function.JsonSchema.GetRawText()) + 128;
        }
        // UTF-8 bytes are a conservative upper bound for text tokenization; include protocol/schema overhead.
        var reservation = checked(bytes + output);
        if (reservation > contextTokens) throw new ChatLimitException("The conversation exceeds this model's context limit. Start a new conversation.");
        var id = Guid.NewGuid().ToString("N");
        await repository.ReserveTokensAsync(turnId, id, reservation, actor, token);
        await repository.RecordProviderExposureAsync(turnId, id, actor, token);
        return id;
    }

    // Provider bindings own their client. Disposing a per-turn function pipeline must not dispose the shared binding.
    protected override void Dispose(bool disposing) { }
}
