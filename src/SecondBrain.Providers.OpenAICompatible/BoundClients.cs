using System.Runtime.CompilerServices;
using System.Text.Json;
using System.ClientModel;
using Microsoft.Extensions.AI;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Providers;

namespace SecondBrain.Providers.OpenAICompatible;

internal sealed class BoundChatClient(IChatClient client, string model, int maxOutputTokens) : DelegatingChatClient(client)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        options = BoundOptions(options);
        try { return await InnerClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false); }
        catch (JsonException exception) { throw Malformed(exception); }
        catch (ClientResultException exception) { throw Translate(exception); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested) { throw TimeoutFailure(exception); }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options = BoundOptions(options);
        await using var enumerator = InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool next;
            try { next = await enumerator.MoveNextAsync().ConfigureAwait(false); }
            catch (JsonException exception) { throw Malformed(exception); }
            catch (ClientResultException exception) { throw Translate(exception); }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested) { throw TimeoutFailure(exception); }
            if (!next) yield break;
            yield return enumerator.Current;
        }
    }

    private ChatOptions BoundOptions(ChatOptions? options)
    {
        if (options?.RawRepresentationFactory is not null)
            throw new InvalidOperationException("Bound clients accept neutral chat options only.");
        if (options?.ModelId is not null && options.ModelId != model)
            throw new InvalidOperationException("A bound client cannot override its configured model.");
        if (options?.MaxOutputTokens is <= 0 || options?.MaxOutputTokens > maxOutputTokens)
            throw new InvalidOperationException("Requested output exceeds the resolved model limit.");
        var bounded = options?.Clone() ?? new ChatOptions();
        bounded.MaxOutputTokens ??= maxOutputTokens;
        return bounded;
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType == typeof(ChatClientMetadata)
            ? base.GetService(serviceType, serviceKey) : serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    internal static ProviderRequestException Malformed(Exception exception)
        => new("https://secondbrain.dev/problems/provider-malformed", "Provider returned an invalid response.", innerException: exception);
    internal static ProviderRequestException TimeoutFailure(Exception exception)
        => new("https://secondbrain.dev/problems/provider-timeout", "Provider request timed out.", retryable: true, innerException: exception);

    // HttpClientPipelineTransport wraps handler exceptions; preserve the policy's neutral failure type.
    internal static Exception Translate(ClientResultException exception)
    {
        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is PrivacyPolicyException or ProviderRequestException) return inner;
            if (inner is JsonException) return Malformed(inner);
            if (inner is OperationCanceledException) return TimeoutFailure(inner);
        }
        return new ProviderRequestException("https://secondbrain.dev/problems/provider-response",
            "Provider request failed.", retryable: exception.Status == 429 || exception.Status >= 500,
            innerException: exception);
    }
}

internal sealed class BoundEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> generator,
    string model, int? dimensions, int batchMax) : DelegatingEmbeddingGenerator<string, Embedding<float>>(generator)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (options?.RawRepresentationFactory is not null)
            throw new InvalidOperationException("Bound clients accept neutral embedding options only.");
        if (options?.ModelId is not null && options.ModelId != model ||
            options?.Dimensions is not null && dimensions is not null && options.Dimensions != dimensions)
            throw new InvalidOperationException("A bound generator cannot override its embedding space.");
        var batch = values.Take(batchMax == int.MaxValue ? int.MaxValue : batchMax + 1).ToArray();
        if (batch.Length > batchMax)
            throw new InvalidOperationException("Embedding input batch exceeds the resolved model limit.");
        try { return await InnerGenerator.GenerateAsync(batch, options, cancellationToken).ConfigureAwait(false); }
        catch (JsonException exception) { throw BoundChatClient.Malformed(exception); }
        catch (ClientResultException exception) { throw BoundChatClient.Translate(exception); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested) { throw BoundChatClient.TimeoutFailure(exception); }
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType == typeof(EmbeddingGeneratorMetadata)
            ? base.GetService(serviceType, serviceKey) : serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
}
