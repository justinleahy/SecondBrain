using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Providers;

namespace SecondBrain.Core.Ingestion;

public sealed class EnrichmentProcessor(IEnrichmentRepository repository, IProviderRegistry providers, IModelUsage usage,
    IOptionsMonitor<SecondBrainOptions> options, TimeSpan? callTimeout = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };
    public async Task<bool> ProcessAsync(CancellationToken token = default)
    {
        var snapshot = options.CurrentValue;
        if (!snapshot.Enrichment.Enabled) return false;
        var fingerprint = EnrichmentPolicy.Fingerprint(snapshot);
        var roleForQueue = providers.GetRole(ModelRole.Enrich);
        var work = snapshot.Enrichment.BatchBackfill && roleForQueue.Model.Capabilities.Batch && roleForQueue.Provider.GetBatchProvider(roleForQueue.Model.Model) is not null
            ? await repository.ClaimInteractiveAsync(fingerprint, token) : await repository.ClaimAsync(fingerprint, token);
        if (work is null) return false;
        long? reservation = null; ModelUsageTotals? totals = null;
        try
        {
            if (!await repository.CheckAsync(work, token)) { await repository.FailAsync(work, "superseded", false, token); return true; }
            if (work.CachedResult is { } cached)
            { await repository.PublishAsync(work, cached, token); return true; }
            var role = providers.GetRole(ModelRole.Enrich);
            var client = role.Provider.GetChatClient(role.Model.Model) ?? throw new InvalidOperationException("Enrichment unavailable.");
            var prepared = EnrichmentPrompt.Create(work, role);
            var request = new ChatOptions { MaxOutputTokens = prepared.Request.MaxOutputTokens, Temperature = 0 };
            if (prepared.Request.Schema is { } schema) request.ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "document_enrichment");
            if (prepared.Request.Reasoning is { } reasoning)
                request.Reasoning = new() { Effort = reasoning switch { "low" => ReasoningEffort.Low, "medium" => ReasoningEffort.Medium, "high" => ReasoningEffort.High, _ => null } };
            if (!Current(snapshot, fingerprint) || !await repository.CheckAsync(work, token))
            { await repository.FailAsync(work, "superseded", false, token); return true; }
            var duration = callTimeout ?? TimeSpan.FromSeconds(60);
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(callTimeout));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(duration);
            reservation = await usage.ReserveAsync(new(Guid.NewGuid().ToString("N"), role.Provider.ProviderName, role.Model.Model,
                "enrich", prepared.ReservedTokens,
                work.Actor, work.DocumentId, work.Revision, work.JobId), deadline.Token);
            var response = await client.GetResponseAsync([new(ChatRole.System, prepared.Request.System), new(ChatRole.User, prepared.Request.Input)], request, deadline.Token);
            totals = ModelUsageTotals.From(response.Usage);
            var result = Parse(response.Text, fingerprint);
            if (!Current(snapshot, fingerprint)) { await repository.FailAsync(work, "configuration_changed", false, token); return true; }
            await repository.PublishAsync(work, result, token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { await repository.FailAsync(work, "timeout", true, token); }
        catch (ModelBudgetException) { await repository.FailAsync(work, "budget", true, token); }
        catch (SecondBrain.Core.Auth.AuthorityChangedException) { await repository.FailAsync(work, "authority_changed", false, token); }
        catch (SecondBrain.Core.Privacy.PrivacyPolicyException) { await repository.FailAsync(work, "privacy_policy", false, token); }
        catch (JsonException) { await repository.FailAsync(work, "invalid_response", false, token); }
        catch (ArgumentException) { await repository.FailAsync(work, "invalid_response", false, token); }
        catch (ManagedFileConflictException) { await repository.FailAsync(work, "writeback_conflict", false, token); }
        catch (InvalidDataException) { await repository.FailAsync(work, "writeback_invalid", false, token); }
        catch (IOException) { await repository.FailAsync(work, "writeback_unavailable", true, token); }
        catch (HttpRequestException error) { await repository.FailAsync(work, "provider_unavailable", error is not ProviderRequestException request || request.Retryable, token); }
        catch (InvalidOperationException) { await repository.FailAsync(work, "provider_unavailable", true, token); }
        finally { if (reservation is { } id) await usage.SettleAsync(id, totals, CancellationToken.None); }
        return true;
    }
    private bool Current(SecondBrainOptions snapshot, string fingerprint) => providers.IsCurrentConfiguration(snapshot)
        && options.CurrentValue.Enrichment.Enabled && EnrichmentPolicy.Fingerprint(options.CurrentValue) == fingerprint;
    public static DocumentEnrichment Parse(string response, string fingerprint)
    {
        if (Encoding.UTF8.GetByteCount(response) > 32768) throw new JsonException("Enrichment output is too large.");
        var start = response.IndexOf('{'); var end = response.LastIndexOf('}');
        if (start < 0 || end < start) throw new JsonException("Enrichment output is not JSON.");
        using var document = JsonDocument.Parse(response[start..(end + 1)], new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.RootElement.EnumerateObject().Count())
            throw new JsonException("Enrichment fields are ambiguous.");
        var value = document.RootElement.Deserialize<Output>(Json) ?? throw new JsonException("Enrichment output is empty.");
        var result = new DocumentEnrichment(fingerprint, value.Summary?.Trim() ?? "", value.Tags ?? [], value.Mentions ?? [], value.SuggestedLinks ?? []);
        EnrichmentPolicy.Validate(result);
        return result with { Tags = result.Tags.Select(t => t.Trim().Normalize()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Mentions = result.Mentions.Select(m => new EnrichmentMention(m.Kind, m.Text.Trim().Normalize())).Distinct().ToArray(),
            SuggestedLinks = result.SuggestedLinks.Select(l => new EnrichmentLink(l.Target.Trim().Normalize(), l.Reason.Trim())).Distinct().ToArray() };
    }
    private sealed record Output(string? Summary, string[]? Tags, EnrichmentMention[]? Mentions,
        [property: JsonPropertyName("suggested_links")] EnrichmentLink[]? SuggestedLinks);
}
