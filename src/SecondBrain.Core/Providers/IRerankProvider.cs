using System.Text.Json.Serialization;

namespace SecondBrain.Core.Providers;

public sealed record RerankScore([property: JsonRequired] int Index, [property: JsonRequired] double Score);

/// <summary>Scores each bounded candidate exactly once, using the configured model and policy transport.</summary>
public interface IRerankProvider
{
    ValueTask<IReadOnlyList<RerankScore>> RerankAsync(string model, string query,
        IReadOnlyList<string> candidates, CancellationToken cancellationToken = default);
}
