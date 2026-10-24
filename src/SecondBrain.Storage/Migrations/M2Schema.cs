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
        """;
}
