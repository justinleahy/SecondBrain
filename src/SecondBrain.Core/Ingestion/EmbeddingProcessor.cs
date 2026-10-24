using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using SecondBrain.Core.Providers;

namespace SecondBrain.Core.Ingestion;

public sealed record EmbeddingSpace(string Id, string Fingerprint, string Provider, string Model, int Dimensions, string Status);
public sealed record EmbeddingWork(string DocumentId, long Revision, string GenerationJson, IReadOnlyList<TextChunk> Chunks);
public interface IEmbeddingWorkRepository
{
    ValueTask<EmbeddingSpace> EnsureSpaceAsync(string fingerprint, string provider, string model, int dimensions, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<EmbeddingWork>> PendingAsync(string spaceId, int limit = 32, CancellationToken cancellationToken = default);
    ValueTask<float[]?> CachedAsync(string spaceId, string inputHash, CancellationToken cancellationToken = default);
    ValueTask<bool> SaveAsync(EmbeddingSpace space, EmbeddingWork work, IReadOnlyDictionary<string, float[]> vectors, CancellationToken cancellationToken = default);
    ValueTask<bool> ActivateAsync(string spaceId, CancellationToken cancellationToken = default);
    ValueTask<EmbeddingSpace?> ActiveAsync(CancellationToken cancellationToken = default);
    ValueTask ReconcileActiveAsync(string spaceId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

/// <summary>All inference goes through the configured binding's policy-owned generator.</summary>
public sealed class EmbeddingProcessor(IProviderRegistry providers, IEmbeddingWorkRepository repository)
{
    public async ValueTask<int> ProcessAsync(CancellationToken cancellationToken = default)
    {
        var role = providers.GetRole(ModelRole.Embed);
        var dimensions = role.Model.Limits.EmbedDimensions ?? throw new ArgumentException("Embedding dimensions are required.");
        var maximum = role.Model.Limits.EmbedMaxInputTokens ?? throw new ArgumentException("Embedding input bound is required.");
        if (maximum < 1024) throw new ArgumentException("Embedding input bound requires controlled rechunking.");
        var batchMaximum = role.Model.Limits.EmbedBatchMax ?? 1;
        var fingerprint = EmbeddingFingerprint.Create(role.Provider.ProviderName, role.Provider.Endpoint?.AbsoluteUri, role.Model.Model, dimensions);
        var space = await repository.EnsureSpaceAsync(fingerprint, role.Provider.ProviderName, role.Model.Model, dimensions, cancellationToken);
        var processed = 0;
        var active = await repository.ActiveAsync(cancellationToken);
        if (active is not null && active.Id != space.Id)
        {
            try
            {
                var previous = providers.GetEmbeddingProvider(active.Provider, active.Model, active.Dimensions, active.Fingerprint);
                var previousModel = await previous.ResolveAsync(active.Model, cancellationToken: cancellationToken);
                processed += await ProcessSpaceAsync(active, previous, previousModel.Limits.EmbedMaxInputTokens ?? maximum,
                    previousModel.Limits.EmbedBatchMax ?? batchMaximum, cancellationToken);
            }
            catch (Exception error) when (error is HttpRequestException or SecondBrain.Core.Privacy.PrivacyPolicyException or InvalidOperationException or ArgumentException)
            {
                // Catch-up must continue even when the old provider is unavailable. QueryAsync
                // reports its failure and the search door falls back to lexical retrieval.
            }
        }
        processed += await ProcessSpaceAsync(space, role.Provider, maximum, batchMaximum, cancellationToken);
        if (active?.Id != space.Id) await repository.ActivateAsync(space.Id, cancellationToken);
        else await repository.ReconcileActiveAsync(space.Id, cancellationToken);
        return processed;
    }

    private async ValueTask<int> ProcessSpaceAsync(EmbeddingSpace space, IProviderBinding provider, int maximum, int batchMaximum, CancellationToken cancellationToken)
    {
        var work = await repository.PendingAsync(space.Id, 32, cancellationToken);
        if (work.Count == 0) return 0;
        var generator = provider.GetEmbeddingGenerator(space.Model) ?? throw new InvalidOperationException("Embedding generator unavailable.");
        var processed = 0;
        foreach (var document in work)
        {
            var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
            var missing = new List<TextChunk>();
            foreach (var chunk in document.Chunks)
            {
                var cache = await repository.CachedAsync(space.Id, chunk.InputHash, cancellationToken);
                if (cache is null) missing.Add(chunk); else vectors[chunk.Id] = cache;
            }
            foreach (var batch in missing.Chunk(Math.Max(1, batchMaximum)))
            {
                if (batch.Any(chunk => chunk.TokenCount > maximum)) throw new ArgumentException("Embedding input exceeds resolved bound.");
                var embeddings = await generator.GenerateAsync(batch.Select(chunk => chunk.Context + "\n" + chunk.Text), new EmbeddingGenerationOptions { AdditionalProperties = new() { ["intent"] = "document" } }, cancellationToken);
                if (embeddings.Count != batch.Length) throw new ArgumentException("Embedding response count mismatch.");
                for (var i = 0; i < batch.Length; i++)
                {
                    var vector = embeddings[i].Vector.ToArray();
                    if (vector.Length != space.Dimensions || vector.Any(value => !float.IsFinite(value))) throw new ArgumentException("Invalid embedding vector.");
                    var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
                    if (norm == 0) throw new ArgumentException("Zero embedding vector.");
                    vectors[batch[i].Id] = vector.Select(value => (float)(value / norm)).ToArray();
                }
            }
            if (await repository.SaveAsync(space, document, vectors, cancellationToken)) processed++;
        }
        return processed;
    }

    public async ValueTask<(float[]? Vector, string? SpaceId)> QueryAsync(string query, CancellationToken cancellationToken = default,
        Func<IProviderBinding, EmbeddingSpace, CancellationToken, ValueTask>? beforeSend = null)
    {
        var space = await repository.ActiveAsync(cancellationToken);
        if (space is null) return (null, null);
        var provider = providers.GetEmbeddingProvider(space.Provider, space.Model, space.Dimensions, space.Fingerprint);
        var generator = provider.GetEmbeddingGenerator(space.Model) ?? throw new InvalidOperationException("Embedding generator unavailable.");
        if (beforeSend is not null) await beforeSend(provider, space, cancellationToken);
        var result = await generator.GenerateAsync(new[] { query }, new EmbeddingGenerationOptions { AdditionalProperties = new() { ["intent"] = "query" } }, cancellationToken);
        if (result.Count != 1 || result[0].Vector.Length != space.Dimensions) throw new ArgumentException("Invalid query embedding.");
        return (result[0].Vector.ToArray(), space.Id);
    }
}
