using System.Collections.Concurrent;
using SecondBrain.Core.Providers;

namespace SecondBrain.Providers.OpenAICompatible;

/// <summary>Explicit declarations for concrete model IDs. An endpoint never implies a model or vendor.</summary>
public sealed class ModelCatalog
{
    private readonly ConcurrentDictionary<string, ModelDeclaration> models = new(StringComparer.Ordinal);

    public ModelCatalog()
    {
        Register("mock-chat", new ModelCapabilities { Streaming = true, Tools = true },
            new ModelLimits { ContextTokens = 8192, MaxOutputTokens = 1024 });
        Register("mock-embed", new ModelCapabilities(),
            new ModelLimits { EmbedDimensions = 4, EmbedMaxInputTokens = 8192, EmbedBatchMax = 32 });
        Register("gpt-4o", new ModelCapabilities { Streaming = true, Tools = true, StructuredOutput = true },
            new ModelLimits { ContextTokens = 128000, MaxOutputTokens = 16384 });
        Register("text-embedding-3-small", new ModelCapabilities(),
            new ModelLimits { EmbedDimensions = 1536, EmbedMaxInputTokens = 8191, EmbedBatchMax = 2048 });
    }

    /// <summary>Registers a reviewed concrete model declaration, including models served by local endpoints.</summary>
    public void Register(string model, ModelCapabilities capabilities, ModelLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        models[model] = new(capabilities, limits);
    }

    public ResolvedModel Resolve(string provider, string model, ModelLimits? overrides = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var declaration = models.GetValueOrDefault(model) ?? new(new ModelCapabilities(), new ModelLimits());
        var limits = declaration.Limits;
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
        return new(provider, model, declaration.Capabilities, resolved);
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
