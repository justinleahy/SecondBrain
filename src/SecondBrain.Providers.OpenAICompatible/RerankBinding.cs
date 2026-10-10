using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SecondBrain.Core.Providers;

namespace SecondBrain.Providers.OpenAICompatible;

public sealed partial class OpenAICompatibleBinding
{
    public IRerankProvider? GetRerankProvider(string model) => role == ModelRole.Rerank && model == configuredModel ? this : null;

    public async ValueTask<IReadOnlyList<RerankScore>> RerankAsync(string model, string query,
        IReadOnlyList<string> candidates, CancellationToken cancellationToken = default)
    {
        if (GetRerankProvider(model) is null || candidates.Count is < 1 or > 20)
            throw new ArgumentException("Invalid rerank binding or candidate count.");
        var resolved = await ResolveAsync(model, cancellationToken: cancellationToken);
        if (candidates.Any(candidate => (long)Encoding.UTF8.GetByteCount(query) + Encoding.UTF8.GetByteCount(candidate) + 128 > (resolved.Limits.ContextTokens ?? 0)))
            throw new ArgumentException("Rerank input exceeds resolved context.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint.AbsoluteUri.TrimEnd('/') + "/rerank"))
        {
            Content = JsonContent.Create(new { model, query, documents = candidates, top_n = candidates.Count }),
        };
        using var response = await inferenceClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(bytes, deadline.Token)) > 0)
        {
            if (buffer.Length + count > 65536) throw new JsonException("Oversized rerank response.");
            buffer.Write(bytes, 0, count);
        }
        using var json = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
        if (!json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != candidates.Count)
            throw new JsonException("Invalid rerank results.");
        var scores = new List<RerankScore>();
        foreach (var item in results.EnumerateArray())
        {
            if (!item.TryGetProperty("index", out var index) || !index.TryGetInt32(out var position) || position < 0 || position >= candidates.Count ||
                !item.TryGetProperty("relevance_score", out var score) || !score.TryGetDouble(out var value) || !double.IsFinite(value))
                throw new JsonException("Invalid rerank score.");
            scores.Add(new(position, value));
        }
        if (scores.Select(score => score.Index).Distinct().Count() != candidates.Count) throw new JsonException("Duplicate rerank index.");
        return scores;
    }
}
