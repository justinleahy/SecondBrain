namespace SecondBrain.Storage.Migrations;

internal static class UsageSchema
{
    internal const string Ddl = """
        CREATE TABLE IF NOT EXISTS usage_calls(
          call_id TEXT PRIMARY KEY, usage_id INTEGER NOT NULL UNIQUE REFERENCES usage(id),
          charged_tokens INTEGER NOT NULL CHECK(charged_tokens>=0),
          credential_generation INTEGER, account_epoch INTEGER
        );
        CREATE INDEX IF NOT EXISTS usage_at ON usage(at);
        CREATE INDEX IF NOT EXISTS usage_credential_at ON usage(credential_id,at);
        """;
}
