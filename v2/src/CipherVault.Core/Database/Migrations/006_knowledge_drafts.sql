CREATE TABLE document_drafts (
    draft_id TEXT PRIMARY KEY
        CHECK (length(draft_id) = 36),
    draft_revision INTEGER NOT NULL
        CHECK (draft_revision >= 1),
    draft_type TEXT NOT NULL
        CHECK (draft_type IN ('create', 'update', 'trash')),
    status TEXT NOT NULL DEFAULT 'draft'
        CHECK (
            status IN (
                'draft',
                'pending_review',
                'conflict',
                'applied',
                'rejected',
                'discarded',
                'expired'
            )
        ),
    target_document_id TEXT
        CHECK (
            target_document_id IS NULL
            OR length(target_document_id) = 36
        ),
    base_document_revision INTEGER
        CHECK (
            base_document_revision IS NULL
            OR base_document_revision >= 1
        ),
    payload_version INTEGER NOT NULL DEFAULT 1
        CHECK (payload_version >= 1),
    payload_json TEXT NOT NULL
        CHECK (
            json_valid(payload_json)
            AND json_type(payload_json) = 'object'
            AND length(CAST(payload_json AS BLOB)) <= 4194304
        ),
    base_snapshot_json TEXT
        CHECK (
            base_snapshot_json IS NULL
            OR (
                json_valid(base_snapshot_json)
                AND json_type(base_snapshot_json) = 'object'
                AND length(CAST(base_snapshot_json AS BLOB)) <= 4194304
            )
        ),
    source_actor TEXT NOT NULL
        CHECK (source_actor IN ('user_ui', 'mcp')),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    terminal_at TEXT,
    applied_document_id TEXT
        CHECK (
            applied_document_id IS NULL
            OR length(applied_document_id) = 36
        ),
    applied_document_revision INTEGER
        CHECK (
            applied_document_revision IS NULL
            OR applied_document_revision >= 1
        ),
    applied_at TEXT,
    CHECK (updated_at >= created_at),
    CHECK (
        (
            draft_type = 'create'
            AND target_document_id IS NULL
            AND base_document_revision IS NULL
            AND base_snapshot_json IS NULL
        )
        OR
        (
            draft_type IN ('update', 'trash')
            AND target_document_id IS NOT NULL
            AND base_document_revision IS NOT NULL
            AND base_snapshot_json IS NOT NULL
        )
    ),
    CHECK (
        (
            status IN ('applied', 'rejected', 'discarded', 'expired')
            AND terminal_at IS NOT NULL
            AND terminal_at >= created_at
            AND terminal_at <= updated_at
        )
        OR
        (
            status IN ('draft', 'pending_review', 'conflict')
            AND terminal_at IS NULL
        )
    ),
    CHECK (
        (
            status = 'applied'
            AND applied_document_id IS NOT NULL
            AND applied_document_revision IS NOT NULL
            AND applied_at IS NOT NULL
            AND applied_at = terminal_at
        )
        OR
        (
            status <> 'applied'
            AND applied_document_id IS NULL
            AND applied_document_revision IS NULL
            AND applied_at IS NULL
        )
    ),
    FOREIGN KEY (target_document_id)
        REFERENCES documents(document_id)
        ON DELETE CASCADE
) STRICT;

CREATE INDEX ix_document_drafts_status
    ON document_drafts(status, updated_at DESC, draft_id);

CREATE INDEX ix_document_drafts_target
    ON document_drafts(target_document_id, status, updated_at DESC)
    WHERE target_document_id IS NOT NULL;

CREATE INDEX ix_document_drafts_terminal
    ON document_drafts(terminal_at, draft_id)
    WHERE terminal_at IS NOT NULL;

CREATE TRIGGER trg_document_drafts_revision
BEFORE UPDATE ON document_drafts
WHEN NEW.draft_revision <> OLD.draft_revision + 1
BEGIN
    SELECT RAISE(ABORT, 'document_drafts.draft_revision must increment by one');
END;

CREATE TRIGGER trg_document_drafts_terminal_immutable
BEFORE UPDATE ON document_drafts
WHEN OLD.status IN ('applied', 'rejected', 'discarded', 'expired')
BEGIN
    SELECT RAISE(ABORT, 'terminal document drafts are immutable');
END;

CREATE TRIGGER trg_document_drafts_identity_immutable
BEFORE UPDATE OF
    draft_id,
    draft_type,
    target_document_id,
    source_actor,
    created_at
ON document_drafts
WHEN
    NEW.draft_id <> OLD.draft_id
    OR NEW.draft_type <> OLD.draft_type
    OR NEW.target_document_id IS NOT OLD.target_document_id
    OR NEW.source_actor <> OLD.source_actor
    OR NEW.created_at <> OLD.created_at
BEGIN
    SELECT RAISE(ABORT, 'document draft identity fields are immutable');
END;

CREATE TABLE idempotency_records (
    idempotency_key TEXT PRIMARY KEY
        CHECK (length(idempotency_key) = 36),
    actor_kind TEXT NOT NULL
        CHECK (actor_kind IN ('user_ui', 'mcp')),
    operation TEXT NOT NULL
        CHECK (
            operation IN (
                'document.create',
                'document.append',
                'document.update',
                'document.patch',
                'document.delete',
                'draft.create',
                'draft.update',
                'draft.rebase',
                'draft.submit_review',
                'draft.reject',
                'draft.discard',
                'draft.apply',
                'mcp_write_mode.change'
            )
        ),
    session_id TEXT
        CHECK (
            session_id IS NULL
            OR length(session_id) = 36
        ),
    request_hash TEXT NOT NULL
        CHECK (
            length(request_hash) = 64
            AND request_hash NOT GLOB '*[^0-9a-f]*'
        ),
    result_json TEXT NOT NULL
        CHECK (
            json_valid(result_json)
            AND json_type(result_json) = 'object'
            AND length(CAST(result_json AS BLOB)) <= 65536
        ),
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    CHECK (
        (
            operation = 'mcp_write_mode.change'
            AND session_id IS NOT NULL
        )
        OR
        (
            operation <> 'mcp_write_mode.change'
            AND session_id IS NULL
        )
    ),
    CHECK (expires_at >= created_at)
) STRICT;

CREATE INDEX ix_idempotency_expiry
    ON idempotency_records(expires_at, idempotency_key);

CREATE TRIGGER trg_idempotency_no_update
BEFORE UPDATE ON idempotency_records
BEGIN
    SELECT RAISE(ABORT, 'idempotency_records is immutable');
END;

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
            'export.plaintext',
            'mcp_write_mode.change',
            'draft.create',
            'draft.update',
            'draft.rebase',
            'draft.submit_review',
            'draft.reject',
            'draft.discard',
            'draft.apply',
            'draft.expire',
            'draft.cleanup'
        )
    ),
    document_id TEXT
        CHECK (document_id IS NULL OR length(document_id) = 36),
    revision INTEGER
        CHECK (revision IS NULL OR revision >= 1),
    correlation_id TEXT
        CHECK (correlation_id IS NULL OR length(correlation_id) <= 128),
    result TEXT NOT NULL
        CHECK (result IN ('success', 'failed', 'cancelled', 'started')),
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

CREATE TEMP TABLE audit_fk_guard (
    value INTEGER NOT NULL CHECK (value = 0)
) STRICT;

INSERT INTO audit_fk_guard(value)
SELECT count(*) FROM pragma_foreign_key_check;

DROP TABLE audit_fk_guard;

CREATE TEMP TABLE audit_integrity_guard (
    value INTEGER NOT NULL CHECK (value = 0)
) STRICT;

INSERT INTO audit_integrity_guard(value)
SELECT count(*)
FROM pragma_integrity_check
WHERE integrity_check <> 'ok';

DROP TABLE audit_integrity_guard;
