namespace SecondBrain.Storage.Migrations;

internal static class M4Schema
{
    internal const string StateDdl = """
        CREATE TABLE IF NOT EXISTS retention_removed_at (
          document_id TEXT PRIMARY KEY REFERENCES documents(id), since TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS retention_purges (
          document_id TEXT PRIMARY KEY REFERENCES documents(id), operation_id TEXT NOT NULL,
          reason TEXT NOT NULL, cutoff TEXT NOT NULL, completed_at TEXT
        );
        CREATE TABLE IF NOT EXISTS retention_attempts (
          document_id TEXT PRIMARY KEY REFERENCES documents(id), last_attempt_at TEXT NOT NULL
        );
        INSERT OR IGNORE INTO retention_removed_at(document_id,since)
          SELECT d.id,COALESCE((SELECT MIN(a.since) FROM absences a WHERE a.document_id=d.id AND a.cleared_at IS NULL),strftime('%Y-%m-%dT%H:%M:%fZ','now'))
          FROM documents d WHERE d.status='removed_from_source';
        UPDATE documents SET deleted_at=COALESCE((SELECT MAX(r.created_at) FROM revisions r WHERE r.document_id=documents.id AND r.cause IN ('delete','container-delete')),strftime('%Y-%m-%dT%H:%M:%fZ','now'))
          WHERE status='deleted' AND deleted_at IS NULL;
        CREATE TRIGGER IF NOT EXISTS retention_removed_insert AFTER INSERT ON documents WHEN NEW.status='removed_from_source'
          BEGIN INSERT OR REPLACE INTO retention_removed_at(document_id,since) VALUES(NEW.id,NEW.updated_at); END;
        CREATE TRIGGER IF NOT EXISTS retention_removed_update AFTER UPDATE OF status ON documents WHEN NEW.status='removed_from_source' AND OLD.status<>'removed_from_source'
          BEGIN INSERT OR REPLACE INTO retention_removed_at(document_id,since) VALUES(NEW.id,NEW.updated_at); END;
        CREATE TRIGGER IF NOT EXISTS retention_removed_clear AFTER UPDATE OF status ON documents WHEN NEW.status<>'removed_from_source'
          BEGIN DELETE FROM retention_removed_at WHERE document_id=NEW.id; END;
        CREATE TABLE IF NOT EXISTS calendar_subscriptions (
          source_id TEXT PRIMARY KEY REFERENCES sources(id), container_id TEXT NOT NULL UNIQUE,
          origin TEXT NOT NULL, protected_url TEXT NOT NULL, poll_interval_s INTEGER NOT NULL CHECK(poll_interval_s>=300),
          generation INTEGER NOT NULL DEFAULT 1, requested INTEGER NOT NULL DEFAULT 0, deep_requested INTEGER NOT NULL DEFAULT 0,
          lease_id TEXT, lease_until TEXT, last_attempt_at TEXT, last_success_at TEXT, last_change_at TEXT, next_attempt_at TEXT,
          error_code TEXT, failure_count INTEGER NOT NULL DEFAULT 0, etag TEXT, last_modified TEXT,
          snapshot_hash TEXT, snapshot_path TEXT, rejected_hash TEXT, rejected_generation TEXT
        );
        CREATE TABLE IF NOT EXISTS calendar_members (
          container_id TEXT NOT NULL REFERENCES documents(id), member_key TEXT NOT NULL,
          document_id TEXT NOT NULL UNIQUE REFERENCES documents(id), source_hash TEXT NOT NULL,
          PRIMARY KEY(container_id,member_key)
        );
        CREATE TABLE IF NOT EXISTS calendar_subscription_publications (
          container_id TEXT PRIMARY KEY REFERENCES documents(id), source_id TEXT NOT NULL REFERENCES sources(id),
          source_generation INTEGER NOT NULL, lease_id TEXT NOT NULL, snapshot_hash TEXT NOT NULL, snapshot_path TEXT NOT NULL,
          etag TEXT, last_modified TEXT, accepted_at TEXT NOT NULL, materialization_only INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS calendar_materialization_retry (
          source_id TEXT PRIMARY KEY REFERENCES sources(id), next_attempt_at TEXT NOT NULL, error_code TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS calendar_coverage (
          container_id TEXT PRIMARY KEY REFERENCES documents(id), source_id TEXT NOT NULL,
          covered_from TEXT NOT NULL, covered_to TEXT NOT NULL, time_zone TEXT NOT NULL,
          materialization_generation INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS calendar_batches (
          container_id TEXT PRIMARY KEY REFERENCES documents(id), container_revision INTEGER NOT NULL,
          generation_json TEXT NOT NULL, phase TEXT NOT NULL,
          source_id TEXT NOT NULL, covered_from TEXT NOT NULL, covered_to TEXT NOT NULL,
          time_zone TEXT NOT NULL, materialization_generation INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS calendar_batch_members (
          container_id TEXT NOT NULL REFERENCES calendar_batches(container_id) ON DELETE CASCADE,
          document_id TEXT NOT NULL REFERENCES documents(id), revision INTEGER NOT NULL,
          generation_json TEXT NOT NULL, PRIMARY KEY(container_id,document_id)
        );
        CREATE TABLE IF NOT EXISTS calendar_lifecycle_batches (
          container_id TEXT PRIMARY KEY REFERENCES documents(id), operation TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS calendar_deleted_members (
          container_id TEXT NOT NULL REFERENCES documents(id), document_id TEXT NOT NULL REFERENCES documents(id),
          prior_status TEXT NOT NULL, PRIMARY KEY(container_id,document_id)
        );
        CREATE TABLE IF NOT EXISTS calendar_lifecycle_pauses (
          source_id TEXT PRIMARY KEY REFERENCES sources(id)
        );
        CREATE TABLE IF NOT EXISTS calendar_source_purges (
          source_id TEXT PRIMARY KEY REFERENCES sources(id), operation_id TEXT NOT NULL UNIQUE,
          requested_at TEXT NOT NULL, completed_at TEXT, last_attempt_at TEXT, error_code TEXT
        );
        CREATE TABLE IF NOT EXISTS folder_source_purges (
          source_id TEXT PRIMARY KEY REFERENCES sources(id), operation_id TEXT NOT NULL UNIQUE,
          requested_at TEXT NOT NULL, completed_at TEXT, last_attempt_at TEXT, error_code TEXT
        );
        CREATE TABLE IF NOT EXISTS folder_source_purge_attempts (
          document_id TEXT PRIMARY KEY REFERENCES documents(id), last_attempt_at TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS documents_calendar_key ON documents(container_document_id,natural_key)
          WHERE container_document_id IS NOT NULL AND natural_key IS NOT NULL;
        """;
    internal const string IndexDdl = """
        CREATE TABLE IF NOT EXISTS indexed_calendar_members(document_id TEXT PRIMARY KEY,container_id TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS indexed_calendar_container ON indexed_calendar_members(container_id);
        CREATE TABLE IF NOT EXISTS indexed_people_names (
          document_id TEXT NOT NULL, role TEXT NOT NULL, identifier TEXT NOT NULL, normalized_name TEXT NOT NULL,
          PRIMARY KEY(document_id,role,identifier)
        );
        CREATE INDEX IF NOT EXISTS indexed_people_display_name ON indexed_people_names(normalized_name);
        CREATE TABLE IF NOT EXISTS indexed_calendar_coverage (
          container_id TEXT PRIMARY KEY, source_id TEXT NOT NULL,
          covered_from TEXT NOT NULL, covered_to TEXT NOT NULL, time_zone TEXT NOT NULL,
          materialization_generation INTEGER NOT NULL
        );
        """;
}
