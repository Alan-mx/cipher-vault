CREATE TABLE vault_meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE documents (
    rowid INTEGER PRIMARY KEY,
    document_id TEXT NOT NULL UNIQUE,
    title TEXT NOT NULL,
    source_path TEXT NOT NULL,
    source_path_norm TEXT NOT NULL,
    format TEXT NOT NULL CHECK (format IN ('md', 'txt')),
    content TEXT NOT NULL,
    headings_json TEXT NOT NULL DEFAULT '[]',
    tags_json TEXT NOT NULL DEFAULT '[]',
    payload_hmac BLOB NOT NULL,
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision >= 1),
    created_at_ms INTEGER NOT NULL,
    updated_at_ms INTEGER NOT NULL,
    deleted_at_ms INTEGER,
    purge_after_ms INTEGER,
    CHECK (
        (deleted_at_ms IS NULL AND purge_after_ms IS NULL)
        OR
        (deleted_at_ms IS NOT NULL AND purge_after_ms IS NOT NULL)
    )
);

CREATE UNIQUE INDEX ux_documents_active_path
    ON documents(source_path_norm)
    WHERE deleted_at_ms IS NULL;

CREATE INDEX ix_documents_active_updated
    ON documents(updated_at_ms)
    WHERE deleted_at_ms IS NULL;

CREATE INDEX ix_documents_purge_after
    ON documents(purge_after_ms)
    WHERE deleted_at_ms IS NOT NULL;

CREATE TABLE document_revisions (
    revision_id TEXT PRIMARY KEY,
    document_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    title TEXT NOT NULL,
    source_path TEXT NOT NULL,
    format TEXT NOT NULL CHECK (format IN ('md', 'txt')),
    content TEXT NOT NULL,
    headings_json TEXT NOT NULL DEFAULT '[]',
    tags_json TEXT NOT NULL DEFAULT '[]',
    payload_hmac BLOB NOT NULL,
    created_at_ms INTEGER NOT NULL,
    retain_until_ms INTEGER NOT NULL,
    UNIQUE (document_id, revision),
    FOREIGN KEY (document_id)
        REFERENCES documents(document_id)
        ON DELETE CASCADE
);

CREATE INDEX ix_document_revisions_document
    ON document_revisions(document_id, revision DESC);

CREATE INDEX ix_document_revisions_retain
    ON document_revisions(retain_until_ms);

CREATE TABLE tags (
    tag_id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    normalized_name TEXT NOT NULL UNIQUE
);

CREATE TABLE document_tags (
    document_id TEXT NOT NULL,
    tag_id TEXT NOT NULL,
    PRIMARY KEY (document_id, tag_id),
    FOREIGN KEY (document_id)
        REFERENCES documents(document_id)
        ON DELETE CASCADE,
    FOREIGN KEY (tag_id)
        REFERENCES tags(tag_id)
        ON DELETE CASCADE
);

CREATE INDEX ix_document_tags_tag
    ON document_tags(tag_id, document_id);

CREATE VIRTUAL TABLE documents_fts USING fts5(
    document_id UNINDEXED,
    title,
    source_path,
    headings,
    tags,
    content,
    tokenize = 'trigram'
);

CREATE TABLE audit_events (
    event_id TEXT PRIMARY KEY,
    occurred_at_ms INTEGER NOT NULL,
    actor_kind TEXT NOT NULL CHECK (
        actor_kind IN ('user_ui', 'user_cli', 'mcp', 'system')
    ),
    client_name TEXT,
    session_id TEXT,
    action TEXT NOT NULL,
    document_id TEXT,
    result TEXT NOT NULL CHECK (
        result IN ('success', 'denied', 'failed', 'cancelled')
    ),
    bytes_returned INTEGER,
    details_json TEXT NOT NULL DEFAULT '{}'
);

CREATE INDEX ix_audit_occurred
    ON audit_events(occurred_at_ms);

CREATE INDEX ix_audit_document
    ON audit_events(document_id, occurred_at_ms);

CREATE TRIGGER trg_audit_no_update
BEFORE UPDATE ON audit_events
BEGIN
    SELECT RAISE(ABORT, 'audit_events is append-only');
END;

CREATE TRIGGER trg_audit_no_delete
BEFORE DELETE ON audit_events
BEGIN
    SELECT RAISE(ABORT, 'audit_events is append-only');
END;

CREATE TABLE import_jobs (
    job_id TEXT PRIMARY KEY,
    source_root TEXT NOT NULL,
    state TEXT NOT NULL CHECK (
        state IN ('preview', 'importing', 'completed', 'failed', 'cancelled')
    ),
    total_documents INTEGER NOT NULL DEFAULT 0,
    processed_documents INTEGER NOT NULL DEFAULT 0,
    skipped_documents INTEGER NOT NULL DEFAULT 0,
    started_at_ms INTEGER NOT NULL,
    finished_at_ms INTEGER,
    error_json TEXT NOT NULL DEFAULT '{}'
);

CREATE TABLE import_items (
    job_id TEXT NOT NULL,
    source_path_norm TEXT NOT NULL,
    payload_hmac BLOB NOT NULL,
    document_id TEXT,
    state TEXT NOT NULL CHECK (
        state IN ('pending', 'imported', 'skipped', 'failed')
    ),
    error_json TEXT NOT NULL DEFAULT '{}',
    PRIMARY KEY (job_id, source_path_norm),
    FOREIGN KEY (job_id)
        REFERENCES import_jobs(job_id)
        ON DELETE CASCADE
);

CREATE INDEX ix_import_items_state
    ON import_items(job_id, state);

CREATE TABLE export_jobs (
    job_id TEXT PRIMARY KEY,
    destination_path TEXT NOT NULL,
    state TEXT NOT NULL CHECK (
        state IN ('preparing', 'exporting', 'completed', 'failed', 'cancelled')
    ),
    total_documents INTEGER NOT NULL DEFAULT 0,
    exported_documents INTEGER NOT NULL DEFAULT 0,
    started_at_ms INTEGER NOT NULL,
    finished_at_ms INTEGER,
    residual_report_json TEXT NOT NULL DEFAULT '{}',
    error_json TEXT NOT NULL DEFAULT '{}'
);

PRAGMA user_version = 1;
