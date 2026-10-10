namespace SecondBrain.Storage.Migrations;

internal static class M1Schema
{
    internal const string IndexDdl = """
        CREATE TABLE IF NOT EXISTS indexed_document_sources(document_id TEXT PRIMARY KEY,source_id TEXT NOT NULL,path TEXT,mime TEXT);
        CREATE TABLE IF NOT EXISTS document_text (
          document_id     TEXT PRIMARY KEY,
          revision        INTEGER NOT NULL,               -- indexed_revision
          generation_json TEXT NOT NULL,                  -- indexed_generation
          lossless_text   TEXT,                           -- what brain_get, the viewer, and citations use
          search_text     TEXT,                           -- normalized view
          offset_map      BLOB,                           -- search offsets → lossless offsets
          summary         TEXT,
          language        TEXT,
          indexed_at      TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS chunks (
          rowid        INTEGER PRIMARY KEY,               -- shared with chunks_fts and the vector slot table
          id           TEXT NOT NULL UNIQUE,
          document_id  TEXT NOT NULL,
          revision     INTEGER NOT NULL,
          ordinal      INTEGER NOT NULL,
          context      TEXT NOT NULL,                     -- "Title > H1 > H2" or "Speaker @ 00:12:04–00:13:10"
          text         TEXT NOT NULL,                     -- lossless slice
          start_offset INTEGER NOT NULL, end_offset INTEGER NOT NULL,
          page         INTEGER,
          segment_from INTEGER, segment_to INTEGER,
          token_count  INTEGER NOT NULL,
          input_hash   TEXT NOT NULL,                     -- sha256(context + "\n" + text): the exact embedding input
          UNIQUE (document_id, ordinal)
        );

        -- External-content FTS over chunks; rows are inserted and deleted explicitly inside the index transaction.
        CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
          text, context, title,
          content = 'chunks_fts_source', content_rowid = 'rowid',
          tokenize = 'unicode61 remove_diacritics 2'
        );
        CREATE TABLE IF NOT EXISTS chunks_fts_source (rowid INTEGER PRIMARY KEY, text TEXT, context TEXT, title TEXT);

        CREATE TABLE IF NOT EXISTS segments (
          document_id  TEXT NOT NULL,
          ordinal      INTEGER NOT NULL,
          speaker      TEXT,                              -- NULL when unknown
          start_ms     INTEGER, end_ms INTEGER,
          start_offset INTEGER NOT NULL, end_offset INTEGER NOT NULL,
          PRIMARY KEY (document_id, ordinal)
        );

        CREATE TABLE IF NOT EXISTS space_coverage (                     -- descriptors live in state.embedding_spaces
          space_id         TEXT PRIMARY KEY,
          chunk_generation INTEGER NOT NULL,
          coverage_total   INTEGER NOT NULL DEFAULT 0,    -- live chunks of the current generation
          coverage_done    INTEGER NOT NULL DEFAULT 0,
          updated_at       TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS chunk_vectors (                      -- one row per space; committed in the chunk's transaction
          space_id    TEXT NOT NULL,                      -- descriptor in state.embedding_spaces
          chunk_rowid INTEGER NOT NULL REFERENCES chunks(rowid) ON DELETE CASCADE,
          vector      BLOB NOT NULL,                      -- unit-normalized float32
          PRIMARY KEY (space_id, chunk_rowid)
        );
        -- Chunk replacement cascades by child rowid, independent of embedding space.
        -- The (space_id, chunk_rowid) primary key cannot support that lookup on its own.
        CREATE INDEX IF NOT EXISTS chunk_vectors_chunk_rowid ON chunk_vectors(chunk_rowid);

        CREATE TABLE IF NOT EXISTS embedding_cache (
          space_id   TEXT NOT NULL,
          input_hash TEXT NOT NULL,
          vector     BLOB NOT NULL,
          created_at TEXT NOT NULL,
          PRIMARY KEY (space_id, input_hash)
        );
        -- Re-publication looks up an exact embedding input across retained spaces.
        CREATE INDEX IF NOT EXISTS embedding_cache_input_hash ON embedding_cache(input_hash);
        -- Durable document lineage of every cached input; it outlives chunk replacement and deletion until purge.
        CREATE TABLE IF NOT EXISTS embedding_cache_owners (
          input_hash  TEXT NOT NULL,
          document_id TEXT NOT NULL,
          PRIMARY KEY (input_hash, document_id)
        );
        CREATE INDEX IF NOT EXISTS embedding_cache_owners_document ON embedding_cache_owners(document_id);
        CREATE INDEX IF NOT EXISTS chunks_input_hash ON chunks(input_hash);
        -- Once per index: attribute existing cache rows to their live chunks, then reclaim rows that predate
        -- lineage and that no chunk references, since no later purge could attribute them to their document.
        INSERT OR IGNORE INTO embedding_cache_owners(input_hash,document_id)
          SELECT DISTINCT c.input_hash,c.document_id FROM chunks c
          WHERE EXISTS(SELECT 1 FROM embedding_cache e WHERE e.input_hash=c.input_hash) AND NOT EXISTS(SELECT 1 FROM index_meta WHERE key='embedding_cache_lineage');
        DELETE FROM embedding_cache WHERE NOT EXISTS(SELECT 1 FROM index_meta WHERE key='embedding_cache_lineage')
          AND NOT EXISTS(SELECT 1 FROM chunks c WHERE c.input_hash=embedding_cache.input_hash);
        INSERT OR IGNORE INTO index_meta(key,value) VALUES('embedding_cache_lineage','1');

        CREATE TABLE IF NOT EXISTS event_occurrences (                  -- materialized within the coverage window; the records calendar queries return
          instance_id   TEXT PRIMARY KEY,                 -- <document_id>@<starts_at>
          document_id   TEXT NOT NULL,                    -- master or exception document
          recurrence_id TEXT NOT NULL DEFAULT '',
          starts_at     TEXT NOT NULL, ends_at TEXT NOT NULL,
          all_day       INTEGER NOT NULL DEFAULT 0,
          status        TEXT NOT NULL,                    -- confirmed | tentative | cancelled
          materialization_generation INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS event_occurrences_time ON event_occurrences(starts_at, ends_at);

        CREATE TABLE IF NOT EXISTS people (                             -- projection of props plus user_people
          document_id  TEXT NOT NULL,
          role         TEXT NOT NULL,                     -- author | organizer | attendee | participant | speaker
          identifier   TEXT NOT NULL,                     -- normalized
          display_name TEXT,
          PRIMARY KEY (document_id, role, identifier)
        );
        CREATE INDEX IF NOT EXISTS people_identifier ON people(identifier);

        CREATE TABLE IF NOT EXISTS tag_assertions (                     -- frontmatter and llm origins; user_tags live in state
          document_id TEXT NOT NULL, tag TEXT NOT NULL, origin TEXT NOT NULL,
          PRIMARY KEY (document_id, tag, origin)
        );
        CREATE TABLE IF NOT EXISTS effective_tags (document_id TEXT NOT NULL, tag TEXT NOT NULL, PRIMARY KEY (document_id, tag));

        CREATE TABLE IF NOT EXISTS links (                              -- parsed; user_links live in state
          from_document_id TEXT NOT NULL,
          to_document_id   TEXT,                          -- NULL while dangling
          target_text      TEXT NOT NULL,
          kind             TEXT NOT NULL,                 -- wikilink | markdown
          PRIMARY KEY (from_document_id, kind, target_text)
        );

        CREATE TABLE IF NOT EXISTS mentions (document_id TEXT NOT NULL, kind TEXT NOT NULL, text TEXT NOT NULL, PRIMARY KEY (document_id, kind, text));

        CREATE TABLE IF NOT EXISTS indexed_props (                      -- hot typed fields for structured predicates
          document_id TEXT NOT NULL, field TEXT NOT NULL,
          value_text TEXT, value_num REAL,
          PRIMARY KEY (document_id, field)
        );
        CREATE INDEX IF NOT EXISTS indexed_props_text ON indexed_props(field, value_text);
        CREATE INDEX IF NOT EXISTS indexed_props_num  ON indexed_props(field, value_num);

        CREATE TABLE IF NOT EXISTS extraction_cache (
          content_hash TEXT NOT NULL, extractor TEXT NOT NULL, extractor_version INTEGER NOT NULL,
          type TEXT NOT NULL, schema_version INTEGER NOT NULL,
          path TEXT NOT NULL, created_at TEXT NOT NULL,
          PRIMARY KEY (content_hash, extractor, extractor_version, type, schema_version)
        );

        CREATE TABLE IF NOT EXISTS retrieval_traces (id INTEGER PRIMARY KEY, at TEXT NOT NULL, query_json TEXT NOT NULL, trace_json TEXT NOT NULL);
        """;
    internal const string StateDdl = """
        CREATE TABLE IF NOT EXISTS folder_scan_candidates(source_id TEXT NOT NULL REFERENCES sources(id),path TEXT NOT NULL,config_hash TEXT NOT NULL,candidate_json TEXT NOT NULL,ordinal INTEGER NOT NULL,created_at TEXT NOT NULL,PRIMARY KEY(source_id,path));
        CREATE TABLE IF NOT EXISTS document_projections(document_id TEXT PRIMARY KEY REFERENCES documents(id), revision INTEGER NOT NULL, generation_json TEXT NOT NULL, payload_id TEXT NOT NULL REFERENCES payloads(id));
        CREATE TABLE IF NOT EXISTS m1_migration_state(version INTEGER PRIMARY KEY,phase TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS jobs_document_kind ON jobs(document_id, kind);
        """;
}
