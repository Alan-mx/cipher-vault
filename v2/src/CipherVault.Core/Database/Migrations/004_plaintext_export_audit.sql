CREATE TABLE audit_events_new (
    event_id TEXT PRIMARY KEY
        CHECK (length(event_id) = 36),
    occurred_at TEXT NOT NULL,
    actor_kind TEXT NOT NULL
        CHECK (actor_kind IN ('user_ui', 'mcp', 'system')),
    action TEXT NOT NULL CHECK (
        action IN (
            'create',
            'update',
            'patch',
            'delete',
            'restore',
            'purge',
            'revision_restore',
            'import_start',
            'import_complete',
            'import_failed',
            'revision_cleanup',
            'link_rebuild',
            'backup.create',
            'backup.download',
            'export.plaintext'
        )
    ),
    document_id TEXT
        CHECK (document_id IS NULL OR length(document_id) = 36),
    revision INTEGER
        CHECK (revision IS NULL OR revision >= 1),
    correlation_id TEXT
        CHECK (correlation_id IS NULL OR length(correlation_id) <= 128),
    result TEXT NOT NULL
        CHECK (result IN ('success', 'failed', 'cancelled')),
    details_json TEXT NOT NULL DEFAULT '{}'
        CHECK (
            json_valid(details_json)
            AND json_type(details_json) = 'object'
            AND length(details_json) <= 4096
        )
) STRICT;

INSERT INTO audit_events_new(
    event_id,
    occurred_at,
    actor_kind,
    action,
    document_id,
    revision,
    correlation_id,
    result,
    details_json
)
SELECT
    event_id,
    occurred_at,
    actor_kind,
    action,
    document_id,
    revision,
    correlation_id,
    result,
    details_json
FROM audit_events;

CREATE TEMP TABLE audit_migration_guard (
    value INTEGER NOT NULL CHECK (value = 0)
) STRICT;

INSERT INTO audit_migration_guard(value)
SELECT
    (SELECT count(*) FROM audit_events_new)
    - (SELECT count(*) FROM audit_events);

DROP TABLE audit_migration_guard;
DROP TABLE audit_events;
ALTER TABLE audit_events_new RENAME TO audit_events;

CREATE INDEX ix_audit_occurred
    ON audit_events(occurred_at DESC, event_id);

CREATE INDEX ix_audit_document
    ON audit_events(document_id, occurred_at DESC);

CREATE INDEX ix_audit_action
    ON audit_events(action, occurred_at DESC);

CREATE INDEX ix_audit_correlation
    ON audit_events(correlation_id, occurred_at DESC)
    WHERE correlation_id IS NOT NULL;

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
