using Microsoft.Extensions.AI;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Providers;
using Xunit;

namespace SecondBrain.Core.Tests.Ingestion;

public sealed class EmbeddingProcessorTests
{
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task IdleProcessingActivatesOnlyAnInactiveSpace(bool active, int expectedActivations)
    {
        var registry = new Registry();
        var repository = new Repository(active);
        var processor = new EmbeddingProcessor(registry, repository);
        Assert.Equal(0, await processor.ProcessAsync());
        Assert.Equal(expectedActivations, repository.Activations);
        Assert.Equal(0, registry.Binding.GeneratorRequests);
        Assert.Equal(active ? 1 : 0, repository.Reconciliations);
    }

    private sealed class Repository(bool active) : IEmbeddingWorkRepository
    {
        private readonly EmbeddingSpace space = new("space", "fingerprint", "local", "model", 2, active ? "active" : "building");
        public int Activations { get; private set; }
        public int Reconciliations { get; private set; }
        public ValueTask ReconcileActiveAsync(string spaceId, CancellationToken cancellationToken = default) { Reconciliations++; return ValueTask.CompletedTask; }
        public ValueTask<EmbeddingSpace> EnsureSpaceAsync(string fingerprint, string provider, string model, int dimensions, CancellationToken cancellationToken = default) => ValueTask.FromResult(space);
        public ValueTask<EmbeddingSpace?> ActiveAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<EmbeddingSpace?>(active ? space : null);
        public ValueTask<IReadOnlyList<EmbeddingWork>> PendingAsync(string spaceId, int limit = 32, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<EmbeddingWork>>([]);
        public ValueTask<bool> ActivateAsync(string spaceId, CancellationToken cancellationToken = default) { Activations++; return ValueTask.FromResult(true); }
        public ValueTask<float[]?> CachedAsync(string spaceId, string inputHash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> SaveAsync(EmbeddingSpace space, EmbeddingWork work, IReadOnlyDictionary<string, float[]> vectors, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Registry : IProviderRegistry
    {
        public Binding Binding { get; } = new();
        public bool IsCurrentConfiguration(SecondBrainOptions options) => true;
        public ProviderRoleBinding GetRole(ModelRole role) => new(role, Binding, new("local", "model", new(), new() { EmbedDimensions = 2, EmbedMaxInputTokens = 1024 }));
        public IProviderBinding GetProvider(string name) => Binding;
        public IReadOnlyList<string> ProviderNames => ["local"];
        public ValueTask<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyDictionary<ModelRole, ProviderHealthResult>> TestAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Binding : IProviderBinding
    {
        public string ProviderName => "local";
        public Uri? Endpoint => null;
        public bool IsLocal => true;
        public int GeneratorRequests { get; private set; }
        public ValueTask<ResolvedModel> ResolveAsync(string model, ModelLimits? limitsOverrides = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ProviderHealthResult> HealthCheckAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IChatClient? GetChatClient(string model) => null;
        public IEmbeddingGenerator<string, Embedding<float>>? GetEmbeddingGenerator(string model) { GeneratorRequests++; return null; }
    }
}
