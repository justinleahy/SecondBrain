namespace SecondBrain.Storage.Migrations;

internal static class EnrichmentSchema
{
    internal const string StateDdl = "CREATE INDEX IF NOT EXISTS jobs_enrichment_document ON jobs(document_id,revision,generation_json) WHERE kind='enrich';";
    internal const string IndexDdl = """
        CREATE TABLE IF NOT EXISTS enrichment_links(
          document_id TEXT NOT NULL, target_text TEXT NOT NULL, reason TEXT NOT NULL,
          PRIMARY KEY(document_id,target_text)
        );
        """;
}
