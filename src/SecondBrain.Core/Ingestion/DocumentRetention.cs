namespace SecondBrain.Core.Ingestion;

/// <summary>Trusted retention policy attached to an optimistic document mutation, never caller-supplied content.</summary>
public sealed record RetentionExpiry(string Status, DateTimeOffset Before, string OperationId)
{
    public void Validate()
    {
        if (Status is not ("deleted" or "removed_from_source") || OperationId.Length is < 1 or > 128 || OperationId.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Invalid retention expiry condition.");
    }
}

public sealed record DocumentRetentionResult(int Purged, int ExpiredIdentities, int Deferred, bool MoreWork);
public interface IDocumentRetention
{
    ValueTask<DocumentRetentionResult> RunAsync(IngestionAuthority? actor = null, int limit = 16, CancellationToken cancellationToken = default);
}

public sealed record RetentionResult(MetadataRetentionResult Metadata, DocumentRetentionResult Documents)
{
    public SecondBrain.Core.Operations.BackupRetentionResult? Backups { get; init; }
    public SecondBrain.Core.Operations.OperationCleanupResult? Operations { get; init; }
}
