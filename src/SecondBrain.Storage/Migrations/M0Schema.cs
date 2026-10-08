namespace SecondBrain.Storage.Migrations;

// M0 subset copied from spec.md v0.6 Appendix A; later projections arrive with their milestones.
internal static class M0Schema
{
    internal const int Version = 1;

    internal const string StateDdl = """
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS sources (
          id               TEXT PRIMARY KEY,
          kind             TEXT NOT NULL,
          name             TEXT NOT NULL,
          config_json      TEXT NOT NULL DEFAULT '{}',
          status           TEXT NOT NULL DEFAULT 'active',
          last_scan_at     TEXT,
          last_full_scan_at TEXT,
          created_at       TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS documents (
          id               TEXT PRIMARY KEY,
          source_id        TEXT NOT NULL REFERENCES sources(id),
          type             TEXT NOT NULL DEFAULT 'file',
          type_origin      TEXT NOT NULL,
          type_confidence  REAL,
          schema_version   INTEGER NOT NULL DEFAULT 1,
          props_json       TEXT NOT NULL DEFAULT '{}',
          props_original_json TEXT NOT NULL DEFAULT '{}',
          title            TEXT NOT NULL,
          mime             TEXT,
          source_url       TEXT,
          provenance       TEXT NOT NULL,
          claimed_client   TEXT,
          occurred_at      TEXT,
          occurred_offset  TEXT,
          occurred_basis   TEXT,
          occurred_precision TEXT,
          natural_key      TEXT,
          content_hash     TEXT NOT NULL,
          size_bytes       INTEGER,
          revision         INTEGER NOT NULL DEFAULT 1,
          publish_state    TEXT NOT NULL DEFAULT 'published',
          indexed_revision INTEGER,
          status           TEXT NOT NULL,
          error            TEXT,
          created_at       TEXT NOT NULL,
          updated_at       TEXT NOT NULL,
          deleted_at       TEXT
        );

        CREATE INDEX IF NOT EXISTS documents_type_occurred ON documents(type, occurred_at);
        CREATE INDEX IF NOT EXISTS documents_updated ON documents(updated_at);
        CREATE INDEX IF NOT EXISTS documents_status ON documents(status);

        CREATE UNIQUE INDEX IF NOT EXISTS documents_natural_key ON documents(source_id, natural_key) WHERE natural_key IS NOT NULL;

        CREATE TABLE IF NOT EXISTS occurrences (
          document_id      TEXT NOT NULL REFERENCES documents(id),
          source_id        TEXT NOT NULL REFERENCES sources(id),
          path             TEXT NOT NULL,
          container_document_id TEXT REFERENCES documents(id),
          member_key       TEXT NOT NULL DEFAULT '',
          file_mtime       TEXT,
          size_bytes       INTEGER,
          last_seen_at     TEXT,
          PRIMARY KEY (source_id, path, member_key)
        );

        CREATE INDEX IF NOT EXISTS occurrences_document ON occurrences(document_id);

        CREATE TABLE IF NOT EXISTS revisions (
          document_id  TEXT NOT NULL REFERENCES documents(id),
          revision     INTEGER NOT NULL,
          cause        TEXT NOT NULL,
          content_hash TEXT NOT NULL,
          created_at   TEXT NOT NULL,
          PRIMARY KEY (document_id, revision)
        );

        CREATE TABLE IF NOT EXISTS overrides (
          document_id TEXT NOT NULL REFERENCES documents(id),
          kind        TEXT NOT NULL,
          key         TEXT NOT NULL DEFAULT '',
          value_json  TEXT NOT NULL,
          created_at  TEXT NOT NULL,
          PRIMARY KEY (document_id, kind, key)
        );

        CREATE TABLE IF NOT EXISTS suppressions (
          id         TEXT PRIMARY KEY,
          scope      TEXT NOT NULL,
          source_id  TEXT REFERENCES sources(id),
          key        TEXT NOT NULL,
          reason     TEXT NOT NULL,
          created_at TEXT NOT NULL,
          UNIQUE (scope, source_id, key)
        );

        CREATE TABLE IF NOT EXISTS absences (
          document_id TEXT NOT NULL REFERENCES documents(id),
          source_id   TEXT NOT NULL REFERENCES sources(id),
          path        TEXT NOT NULL,
          member_key  TEXT NOT NULL DEFAULT '',
          since       TEXT NOT NULL,
          cleared_at  TEXT,
          PRIMARY KEY (document_id, source_id, path, member_key)
        );

        CREATE TABLE IF NOT EXISTS embedding_spaces (
          id               TEXT PRIMARY KEY,
          fingerprint      TEXT NOT NULL UNIQUE,
          provider         TEXT NOT NULL, model TEXT NOT NULL, model_revision TEXT,
          dimensions       INTEGER NOT NULL,
          chunk_generation INTEGER NOT NULL,
          status           TEXT NOT NULL,
          created_at       TEXT NOT NULL, activated_at TEXT, retired_at TEXT
        );

        CREATE TABLE IF NOT EXISTS conversations (
          id                  TEXT PRIMARY KEY,
          title               TEXT,
          owner_kind          TEXT NOT NULL,
          owner_credential_id TEXT REFERENCES credentials(id),
          pinned              INTEGER NOT NULL DEFAULT 0,
          created_at          TEXT NOT NULL, updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS turns (
          id              TEXT PRIMARY KEY,
          conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
          ordinal         INTEGER NOT NULL,
          status          TEXT NOT NULL,
          initiating_credential_id TEXT NOT NULL REFERENCES credentials(id),
          credential_generation INTEGER NOT NULL,
          account_epoch   INTEGER NOT NULL,
          allowed_tools_json TEXT NOT NULL,
          retrieved_content INTEGER NOT NULL DEFAULT 0,
          provider        TEXT, model TEXT,
          degraded_json   TEXT,
          usage_json      TEXT,
          started_at      TEXT NOT NULL, ended_at TEXT,
          UNIQUE (conversation_id, ordinal)
        );

        CREATE TABLE IF NOT EXISTS payloads (
          id                 TEXT PRIMARY KEY,
          sha256             TEXT NOT NULL,
          bytes              BLOB NOT NULL,
          document_refs_json TEXT NOT NULL DEFAULT '[]',
          redacted_at        TEXT,
          created_at         TEXT NOT NULL,
          expires_at         TEXT
        );

        CREATE TABLE IF NOT EXISTS payload_refs (
          payload_id    TEXT NOT NULL REFERENCES payloads(id),
          referrer_kind TEXT NOT NULL,
          referrer_id   TEXT NOT NULL,
          PRIMARY KEY (payload_id, referrer_kind, referrer_id)
        );

        CREATE TABLE IF NOT EXISTS tool_executions (
          id            TEXT PRIMARY KEY,
          turn_id       TEXT NOT NULL REFERENCES turns(id) ON DELETE CASCADE,
          tool          TEXT NOT NULL,
          payload_id    TEXT REFERENCES payloads(id),
          args_hash     TEXT NOT NULL,
          args_summary  TEXT,
          status        TEXT NOT NULL,
          result_payload_id TEXT REFERENCES payloads(id),
          result_summary TEXT,
          initiating_credential_id TEXT NOT NULL REFERENCES credentials(id),
          credential_generation INTEGER NOT NULL,
          account_epoch INTEGER NOT NULL,
          committed_at  TEXT
        );

        CREATE TABLE IF NOT EXISTS pending_operations (
          id                    TEXT PRIMARY KEY,
          turn_id               TEXT REFERENCES turns(id),
          creator_credential_id TEXT NOT NULL REFERENCES credentials(id),
          tool                  TEXT NOT NULL,
          tool_schema_version   INTEGER NOT NULL,
          payload_id            TEXT NOT NULL REFERENCES payloads(id),
          payload_hash          TEXT NOT NULL,
          targets_json          TEXT NOT NULL,
          destination           TEXT,
          policy_generation     INTEGER NOT NULL,
          status                TEXT NOT NULL,
          approved_by           TEXT REFERENCES credentials(id),
          execution_id          TEXT REFERENCES tool_executions(id),
          created_at            TEXT NOT NULL, expires_at TEXT NOT NULL, resolved_at TEXT
        );

        CREATE TABLE IF NOT EXISTS mutations (
          id                TEXT PRIMARY KEY,
          kind              TEXT NOT NULL,
          operation_id      TEXT NOT NULL,
          destination       TEXT NOT NULL,
          expected_hash     TEXT,
          resulting_hash    TEXT,
          payload_id        TEXT REFERENCES payloads(id),
          revision_before   INTEGER, revision_after INTEGER,
          status            TEXT NOT NULL,
          result_payload_id TEXT REFERENCES payloads(id),
          created_at        TEXT NOT NULL, updated_at TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS mutations_status ON mutations(status);

        CREATE TABLE IF NOT EXISTS jobs (
          id              TEXT PRIMARY KEY,
          kind            TEXT NOT NULL,
          document_id     TEXT REFERENCES documents(id),
          revision        INTEGER,
          generation_json TEXT NOT NULL,
          initiating_credential_id TEXT REFERENCES credentials(id),
          credential_generation INTEGER,
          account_epoch   INTEGER NOT NULL,
          payload_json    TEXT NOT NULL DEFAULT '{}',
          status          TEXT NOT NULL,
          attempts        INTEGER NOT NULL DEFAULT 0,
          run_after       TEXT,
          lease_until     TEXT,
          error           TEXT,
          created_at      TEXT NOT NULL, updated_at TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS jobs_status ON jobs(status, run_after);

        CREATE TABLE IF NOT EXISTS batch_jobs (
          id TEXT PRIMARY KEY, provider TEXT NOT NULL, remote_id TEXT NOT NULL, kind TEXT NOT NULL,
          status TEXT NOT NULL,
          request_payload_id TEXT REFERENCES payloads(id), result_payload_id TEXT REFERENCES payloads(id),
          created_at TEXT NOT NULL, updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS credentials (
          id               TEXT PRIMARY KEY,
          name             TEXT NOT NULL,
          kind             TEXT NOT NULL,
          verifier         TEXT NOT NULL,
          scopes           TEXT NOT NULL,
          generation       INTEGER NOT NULL DEFAULT 1,
          kid              TEXT NOT NULL,
          account_epoch    INTEGER NOT NULL,
          device           TEXT,
          idle_expires_at  TEXT, absolute_expires_at TEXT,
          stepped_up_at    TEXT,
          created_at       TEXT NOT NULL, last_used_at TEXT, expires_at TEXT, revoked_at TEXT
        );

        CREATE TABLE IF NOT EXISTS account (
          id               INTEGER PRIMARY KEY CHECK (id = 1),
          password_hash    TEXT NOT NULL,
          password_version INTEGER NOT NULL,
          updated_at       TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS passkeys (id TEXT PRIMARY KEY, public_key BLOB NOT NULL, sign_count INTEGER NOT NULL, name TEXT, created_at TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS login_attempts (source TEXT NOT NULL, at TEXT NOT NULL, success INTEGER NOT NULL);

        CREATE INDEX IF NOT EXISTS login_attempts_source ON login_attempts(source, at);

        CREATE TABLE IF NOT EXISTS idempotency (
          key           TEXT NOT NULL,
          credential_id TEXT NOT NULL,
          payload_hash  TEXT NOT NULL,
          response_json TEXT NOT NULL,
          created_at    TEXT NOT NULL,
          PRIMARY KEY (key, credential_id)
        );

        CREATE TABLE IF NOT EXISTS usage (
          id INTEGER PRIMARY KEY, at TEXT NOT NULL, provider TEXT, model TEXT, role TEXT, turn_id TEXT, credential_id TEXT, job_id TEXT,
          reserved_tokens INTEGER, input_tokens INTEGER, output_tokens INTEGER, cached_input_tokens INTEGER,
          cost_usd REAL, settled INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS publication_fences (document_id TEXT PRIMARY KEY REFERENCES documents(id), generation_json TEXT NOT NULL);
        """;

    internal static readonly (string Table, string[] Columns)[] StateTables =
    [
        ("meta", ["key", "value"]),
        ("sources", ["id", "kind", "name", "config_json", "status", "last_scan_at", "last_full_scan_at", "created_at"]),
        ("documents", ["id", "source_id", "type", "type_origin", "type_confidence", "schema_version", "props_json", "props_original_json", "title", "mime", "source_url", "provenance", "claimed_client", "occurred_at", "occurred_offset", "occurred_basis", "occurred_precision", "natural_key", "content_hash", "size_bytes", "revision", "publish_state", "indexed_revision", "status", "error", "created_at", "updated_at", "deleted_at"]),
        ("occurrences", ["document_id", "source_id", "path", "container_document_id", "member_key", "file_mtime", "size_bytes", "last_seen_at"]),
        ("revisions", ["document_id", "revision", "cause", "content_hash", "created_at"]),
        ("overrides", ["document_id", "kind", "key", "value_json", "created_at"]),
        ("suppressions", ["id", "scope", "source_id", "key", "reason", "created_at"]),
        ("absences", ["document_id", "source_id", "path", "member_key", "since", "cleared_at"]),
        ("embedding_spaces", ["id", "fingerprint", "provider", "model", "model_revision", "dimensions", "chunk_generation", "status", "created_at", "activated_at", "retired_at"]),
        ("conversations", ["id", "title", "owner_kind", "owner_credential_id", "pinned", "created_at", "updated_at"]),
        ("turns", ["id", "conversation_id", "ordinal", "status", "initiating_credential_id", "credential_generation", "account_epoch", "allowed_tools_json", "retrieved_content", "provider", "model", "degraded_json", "usage_json", "started_at", "ended_at"]),
        ("payloads", ["id", "sha256", "bytes", "document_refs_json", "redacted_at", "created_at", "expires_at"]),
        ("payload_refs", ["payload_id", "referrer_kind", "referrer_id"]),
        ("tool_executions", ["id", "turn_id", "tool", "payload_id", "args_hash", "args_summary", "status", "result_payload_id", "result_summary", "initiating_credential_id", "credential_generation", "account_epoch", "committed_at"]),
        ("pending_operations", ["id", "turn_id", "creator_credential_id", "tool", "tool_schema_version", "payload_id", "payload_hash", "targets_json", "destination", "policy_generation", "status", "approved_by", "execution_id", "created_at", "expires_at", "resolved_at"]),
        ("mutations", ["id", "kind", "operation_id", "destination", "expected_hash", "resulting_hash", "payload_id", "revision_before", "revision_after", "status", "result_payload_id", "created_at", "updated_at"]),
        ("jobs", ["id", "kind", "document_id", "revision", "generation_json", "initiating_credential_id", "credential_generation", "account_epoch", "payload_json", "status", "attempts", "run_after", "lease_until", "error", "created_at", "updated_at"]),
        ("batch_jobs", ["id", "provider", "remote_id", "kind", "status", "request_payload_id", "result_payload_id", "created_at", "updated_at"]),
        ("credentials", ["id", "name", "kind", "verifier", "scopes", "generation", "kid", "account_epoch", "device", "idle_expires_at", "absolute_expires_at", "stepped_up_at", "created_at", "last_used_at", "expires_at", "revoked_at"]),
        ("account", ["id", "password_hash", "password_version", "updated_at"]),
        ("passkeys", ["id", "public_key", "sign_count", "name", "created_at"]),
        ("login_attempts", ["source", "at", "success"]),
        ("idempotency", ["key", "credential_id", "payload_hash", "response_json", "created_at"]),
        ("usage", ["id", "at", "provider", "model", "role", "turn_id", "credential_id", "job_id", "reserved_tokens", "input_tokens", "output_tokens", "cached_input_tokens", "cost_usd", "settled"]),
        ("publication_fences", ["document_id", "generation_json"]),
    ];

    internal const string IndexDdl = """
        CREATE TABLE IF NOT EXISTS index_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS indexed_documents (
          document_id  TEXT PRIMARY KEY,
          revision     INTEGER NOT NULL,
          type         TEXT NOT NULL,
          title        TEXT NOT NULL,
          props_json   TEXT NOT NULL,
          occurred_at  TEXT, occurred_precision TEXT, ends_at TEXT,
          status       TEXT NOT NULL,
          updated_at   TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS indexed_documents_type_occurred ON indexed_documents(type, occurred_at);

        CREATE TABLE IF NOT EXISTS indexed_document_generations (document_id TEXT PRIMARY KEY, generation_json TEXT NOT NULL);
        """;

    internal static readonly (string Table, string[] Columns)[] IndexTables =
    [
        ("index_meta", ["key", "value"]),
        ("indexed_documents", ["document_id", "revision", "type", "title", "props_json", "occurred_at", "occurred_precision", "ends_at", "status", "updated_at"]),
        ("indexed_document_generations", ["document_id", "generation_json"]),
    ];
}
