namespace SecondBrain.Storage.Migrations;

internal static class M2Schema
{
    // Content lives exclusively in lineage-bearing payloads, so the existing purge journal
    // removes source excerpts, tool output, replay events, and derived assistant messages together.
    internal const string StateDdl = """
        CREATE TABLE IF NOT EXISTS chat_conversation_owners (
          conversation_id TEXT PRIMARY KEY REFERENCES conversations(id) ON DELETE CASCADE,
          account_epoch INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS chat_turn_details (
          turn_id TEXT PRIMARY KEY REFERENCES turns(id) ON DELETE CASCADE,
          credential_id TEXT NOT NULL REFERENCES credentials(id),
          request_id TEXT NOT NULL,
          request_hash TEXT NOT NULL,
          mode TEXT NOT NULL CHECK(mode IN ('chat','ask')),
          policy_json TEXT NOT NULL,
          user_payload_id TEXT NOT NULL REFERENCES payloads(id),
          answer_payload_id TEXT REFERENCES payloads(id),
          failure_code TEXT,
          tool_calls INTEGER NOT NULL DEFAULT 0,
          metadata_records INTEGER NOT NULL DEFAULT 0,
          tool_result_bytes INTEGER NOT NULL DEFAULT 0,
          UNIQUE(credential_id, request_id)
        );
        CREATE UNIQUE INDEX IF NOT EXISTS chat_one_active_turn ON turns(conversation_id) WHERE status='running';
        CREATE TABLE IF NOT EXISTS chat_events (
          turn_id TEXT NOT NULL REFERENCES turns(id) ON DELETE CASCADE,
          sequence INTEGER NOT NULL,
          kind TEXT NOT NULL,
          payload_id TEXT REFERENCES payloads(id),
          created_at TEXT NOT NULL,
          PRIMARY KEY(turn_id,sequence)
        );
        CREATE TABLE IF NOT EXISTS chat_sources (
          turn_id TEXT NOT NULL REFERENCES turns(id) ON DELETE CASCADE,
          number INTEGER NOT NULL,
          document_id TEXT NOT NULL,
          revision INTEGER NOT NULL,
          generation_json TEXT NOT NULL,
          chunk_id TEXT NOT NULL,
          byte_count INTEGER NOT NULL,
          payload_id TEXT NOT NULL REFERENCES payloads(id),
          PRIMARY KEY(turn_id,number),
          UNIQUE(turn_id,document_id,revision,generation_json,chunk_id)
        );
        CREATE TABLE IF NOT EXISTS chat_model_calls (
          turn_id TEXT NOT NULL REFERENCES turns(id) ON DELETE CASCADE,
          call_id TEXT NOT NULL,
          credential_id TEXT NOT NULL,
          reserved_tokens INTEGER NOT NULL CHECK(reserved_tokens>0),
          charged_tokens INTEGER NOT NULL CHECK(charged_tokens>=0),
          status TEXT NOT NULL CHECK(status IN ('reserved','settled')),
          created_at TEXT NOT NULL,
          PRIMARY KEY(turn_id,call_id)
        );
        CREATE INDEX IF NOT EXISTS chat_model_calls_budget ON chat_model_calls(created_at,credential_id);
        -- Content-free accounting survives removal of a conversation, so deletion
        -- cannot refund consumed tokens or replay an old submission as new work.
        CREATE TABLE IF NOT EXISTS chat_usage_history (
          id INTEGER PRIMARY KEY,
          credential_id TEXT NOT NULL,
          charged_tokens INTEGER NOT NULL CHECK(charged_tokens>=0),
          created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS chat_usage_history_budget ON chat_usage_history(created_at,credential_id);
        CREATE TABLE IF NOT EXISTS chat_deleted_requests (
          credential_id TEXT NOT NULL,
          request_id TEXT NOT NULL,
          deleted_at TEXT NOT NULL,
          PRIMARY KEY(credential_id,request_id)
        );
        CREATE TABLE IF NOT EXISTS chat_proposal_details (
          proposal_id TEXT PRIMARY KEY REFERENCES pending_operations(id),
          mutation_id TEXT NOT NULL UNIQUE,
          policy_hash TEXT NOT NULL,
          creator_generation INTEGER NOT NULL,
          creator_epoch INTEGER NOT NULL,
          approver_generation INTEGER,
          approver_epoch INTEGER,
          failure_code TEXT
        );
        CREATE TABLE IF NOT EXISTS chat_summaries (
          conversation_id TEXT PRIMARY KEY REFERENCES conversations(id) ON DELETE CASCADE,
          through_ordinal INTEGER NOT NULL,
          payload_id TEXT NOT NULL REFERENCES payloads(id),
          created_by_turn TEXT NOT NULL REFERENCES turns(id),
          updated_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS cli_pairings (
          id TEXT PRIMARY KEY,
          code TEXT NOT NULL UNIQUE,
          name TEXT NOT NULL,
          poll_verifier TEXT NOT NULL,
          source TEXT NOT NULL,
          secure INTEGER NOT NULL,
          created_at TEXT NOT NULL,
          expires_at TEXT NOT NULL,
          status TEXT NOT NULL CHECK(status IN ('pending','approved','claimed')),
          approver_id TEXT,
          approver_generation INTEGER,
          approver_epoch INTEGER,
          approved_at TEXT
        );
        CREATE TABLE IF NOT EXISTS chat_turn_attempts (
          turn_id TEXT PRIMARY KEY REFERENCES turns(id) ON DELETE CASCADE,
          attempt INTEGER NOT NULL DEFAULT 0,
          initial_context_payload_id TEXT REFERENCES payloads(id)
        );
        INSERT OR IGNORE INTO chat_turn_attempts(turn_id) SELECT turn_id FROM chat_turn_details;
        CREATE TABLE IF NOT EXISTS chat_retries (
          turn_id TEXT NOT NULL REFERENCES turns(id) ON DELETE CASCADE,
          request_id TEXT NOT NULL,
          use_fallback INTEGER NOT NULL,
          attempt INTEGER NOT NULL,
          after_sequence INTEGER NOT NULL,
          PRIMARY KEY(turn_id,request_id)
        );
        CREATE TABLE IF NOT EXISTS chat_proposal_calls (
          proposal_id TEXT PRIMARY KEY REFERENCES pending_operations(id) ON DELETE CASCADE,
          invocation_id TEXT NOT NULL UNIQUE REFERENCES tool_executions(id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS chat_ignored_lineage (
          turn_id TEXT NOT NULL REFERENCES turns(id) ON DELETE CASCADE,
          payload_id TEXT NOT NULL REFERENCES payloads(id) ON DELETE CASCADE,
          PRIMARY KEY(turn_id,payload_id)
        );
        CREATE TABLE IF NOT EXISTS chat_provider_exposures (
          turn_id TEXT NOT NULL,
          call_id TEXT NOT NULL,
          provider TEXT NOT NULL,
          model TEXT NOT NULL,
          endpoint TEXT,
          documents_json TEXT NOT NULL,
          PRIMARY KEY(turn_id,call_id),
          FOREIGN KEY(turn_id,call_id) REFERENCES chat_model_calls(turn_id,call_id) ON DELETE CASCADE
        );
        """;
}
