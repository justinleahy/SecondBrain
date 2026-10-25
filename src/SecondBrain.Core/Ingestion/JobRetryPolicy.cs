namespace SecondBrain.Core.Ingestion;

public static class JobRetryPolicy
{
    public static string? Rejection(string kind, string? error, string status, bool current)
    {
        if (!current) return "job-superseded";
        if (status != "failed") return "job-not-failed";
        if (kind == "enrich") return "enrichment-requires-infer";
        if (kind is "stage" or "chunk" || kind == "extract" && error is not ("extraction-unavailable" or "extraction-timeout" or "provider-unavailable" or "extractor_protocol_failed"))
            return "deterministic-failure";
        return null;
    }
}
