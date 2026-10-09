using System.Collections.Concurrent;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Providers;

namespace SecondBrain.Providers.OpenAICompatible;

/// <summary>Explicit declarations for concrete model IDs. An endpoint never implies a model or vendor.</summary>
public sealed class ModelCatalog
{
    private readonly ConcurrentDictionary<(string Provider, string Model), ModelDeclaration> models = new();

    /// <summary>Registers a reviewed declaration for one explicit provider/model pair.
    /// No vendor metadata is inferred from an alias, adapter kind, endpoint, or familiar model ID.</summary>
    public void Register(string provider, string model, ModelCapabilities capabilities, ModelLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        models[(provider, model)] = new(capabilities, limits);
    }

    /// <summary>
    /// Resolves catalog declarations, then explicit configuration. Configured capabilities
    /// describe only models absent from the catalog; for a catalog model they must agree.
    /// </summary>
    public ResolvedModel Resolve(string provider, string model, ModelLimits? overrides = null,
        ModelCapabilityOptions? declaredCapabilities = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ModelCapabilities capabilities;
        ModelLimits limits;
        if (models.TryGetValue((provider, model), out var declaration))
        {
            capabilities = declaration.Capabilities;
            limits = declaration.Limits;
            if (declaredCapabilities is not null &&
                (declaredCapabilities.Tools is { } tools && tools != capabilities.Tools ||
                 declaredCapabilities.Streaming is { } streaming && streaming != capabilities.Streaming ||
                 declaredCapabilities.StructuredOutput is { } structured && structured != capabilities.StructuredOutput))
                throw new InvalidOperationException("Configured model capabilities contradict the reviewed catalog declaration.");
        }
        else
        {
            capabilities = new ModelCapabilities
            {
                Tools = declaredCapabilities?.Tools ?? false,
                Streaming = declaredCapabilities?.Streaming ?? false,
                StructuredOutput = declaredCapabilities?.StructuredOutput ?? false,
            };
            limits = new ModelLimits();
        }
        var resolved = new ModelLimits
        {
            ContextTokens = overrides?.ContextTokens ?? limits.ContextTokens,
            MaxOutputTokens = overrides?.MaxOutputTokens ?? limits.MaxOutputTokens,
            EmbedDimensions = overrides?.EmbedDimensions ?? limits.EmbedDimensions,
            EmbedMaxInputTokens = overrides?.EmbedMaxInputTokens ?? limits.EmbedMaxInputTokens,
            EmbedBatchMax = overrides?.EmbedBatchMax ?? limits.EmbedBatchMax,
        };
        if (new[] { resolved.ContextTokens, resolved.MaxOutputTokens, resolved.EmbedDimensions,
                resolved.EmbedMaxInputTokens, resolved.EmbedBatchMax }.Any(value => value is <= 0))
            throw new InvalidOperationException("Provider model limits must be positive.");
        return new(provider, model, capabilities, resolved);
    }

    public static void ValidateRole(ModelRole role, ResolvedModel model)
    {
        var limits = model.Limits;
        if (role == ModelRole.Embed)
        {
            if (limits.EmbedDimensions is null || limits.EmbedMaxInputTokens is null || limits.EmbedBatchMax is null)
                throw new InvalidOperationException("Embedding binding has unresolved required limits.");
        }
        else
        {
            if (limits.ContextTokens is null || limits.MaxOutputTokens is null)
                throw new InvalidOperationException("Model binding has unresolved required limits.");
            if (role == ModelRole.Chat && !model.Capabilities.Tools)
                throw new InvalidOperationException("Chat binding does not declare native tools.");
        }
    }

    private sealed record ModelDeclaration(ModelCapabilities Capabilities, ModelLimits Limits);
}
