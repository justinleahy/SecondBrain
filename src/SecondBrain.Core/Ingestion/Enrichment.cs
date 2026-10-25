using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecondBrain.Core.Configuration;

namespace SecondBrain.Core.Ingestion;

public sealed record EnrichmentMention(string Kind, string Text);
public sealed record EnrichmentLink(string Target, string Reason);
public sealed record DocumentEnrichment(string Fingerprint, string Summary, string[] Tags,
    EnrichmentMention[] Mentions, EnrichmentLink[] SuggestedLinks)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool WrittenBack { get; init; }
}
public sealed record EnrichmentWork(string JobId, string Lease, string DocumentId, long Revision, string GenerationJson,
    string Fingerprint, string SourceStamp, string Title, string Text, string[] Tags, IngestionAuthority? Actor,
    DocumentEnrichment? CachedResult = null);
public sealed record EnrichmentJob(string Id, string DocumentId, string Status);

public interface IEnrichmentRepository
{
    Task<EnrichmentJob> RequestAsync(string documentId, IngestionAuthority actor, string fingerprint, CancellationToken token = default);
    Task<EnrichmentWork?> ClaimAsync(string fingerprint, CancellationToken token = default);
    Task<EnrichmentWork?> ClaimBackfillAsync(string fingerprint, CancellationToken token = default) => Task.FromResult<EnrichmentWork?>(null);
    Task<EnrichmentWork?> ClaimInteractiveAsync(string fingerprint, CancellationToken token = default) => ClaimAsync(fingerprint, token);
    Task<bool> WasCompletedAsync(EnrichmentWork work, CancellationToken token = default) => Task.FromResult(false);
    Task<bool> CheckAsync(EnrichmentWork work, CancellationToken token = default);
    Task<bool> PublishAsync(EnrichmentWork work, DocumentEnrichment result, CancellationToken token = default);
    Task FailAsync(EnrichmentWork work, string reason, bool retryable, CancellationToken token = default);
}

public static class EnrichmentPolicy
{
    public static string Fingerprint(SecondBrainOptions options) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        "enrichment-v1\n" + JsonSerializer.Serialize(new { options.Models.Enrich, options.Providers, options.Privacy }))));
    public static void Validate(DocumentEnrichment result)
    {
        static bool Text(string? text, int maximum) => text is not null && text.Length <= maximum && !text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));
        if (!Text(result.Summary, 2048) || string.IsNullOrWhiteSpace(result.Summary)
            || result.Tags is null || result.Tags.Length > 5 || result.Tags.Any(t => !Text(t, 256) || string.IsNullOrWhiteSpace(t))
            || result.Mentions is null || result.Mentions.Length > 32 || result.Mentions.Any(m => m is null || m.Kind is not ("person" or "organization" or "project" or "place") || !Text(m.Text, 256) || string.IsNullOrWhiteSpace(m.Text))
            || result.SuggestedLinks is null || result.SuggestedLinks.Length > 5 || result.SuggestedLinks.Any(l => l is null || !Text(l.Target, 256) || string.IsNullOrWhiteSpace(l.Target)
                || Uri.TryCreate(l.Target, UriKind.Absolute, out _) || !Text(l.Reason, 512)))
            throw new ArgumentException("Invalid enrichment result.");
    }
}
