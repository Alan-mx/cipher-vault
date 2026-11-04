CREATE TABLE document_relations (
    relation_id TEXT PRIMARY KEY
        CHECK (length(relation_id) = 36),
    relation_type TEXT NOT NULL
        CHECK (
            relation_type IN (
                'related',
                'example_of',
                'implements',
                'contrasts_with',
                'depends_on'
            )
        ),
    source_document_id TEXT NOT NULL
        CHECK (length(source_document_id) = 36),
    target_document_id TEXT NOT NULL
        CHECK (length(target_document_id) = 36),
    status TEXT NOT NULL DEFAULT 'active'
        CHECK (status IN ('active', 'retracted')),
    created_by_actor TEXT NOT NULL
        CHECK (created_by_actor = 'user_ui'),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    retracted_at TEXT,
    retracted_by_actor TEXT
        CHECK (
            retracted_by_actor IS NULL
            OR retracted_by_actor = 'user_ui'
        ),
    CHECK (source_document_id <> target_document_id),
    CHECK (updated_at >= created_at),
    CHECK (
        (
            relation_type IN ('related', 'contrasts_with')
            AND source_document_id < target_document_id
        )
        OR relation_type IN (
            'example_of',
            'implements',
            'depends_on'
        )
    ),
    CHECK (
        (
            status = 'active'
            AND retracted_at IS NULL
            AND retracted_by_actor IS NULL
            AND updated_at = created_at
        )
        OR
        (
            status = 'retracted'
            AND retracted_at IS NOT NULL
            AND retracted_by_actor IS NOT NULL
            AND retracted_at >= created_at
            AND updated_at = retracted_at
        )
    ),
    FOREIGN KEY (source_document_id)
        REFERENCES documents(document_id)
        ON DELETE CASCADE,
    FOREIGN KEY (target_document_id)
        REFERENCES documents(document_id)
        ON DELETE CASCADE
) STRICT;

CREATE UNIQUE INDEX ux_document_relations_active_edge
    ON document_relations(
        relation_type,
        source_document_id,
        target_document_id
    )
    WHERE status = 'active';

CREATE INDEX ix_document_relations_source_active
    ON document_relations(source_document_id, relation_type, updated_at DESC)
    WHERE status = 'active';

CREATE INDEX ix_document_relations_target_active
    ON document_relations(target_document_id, relation_type, updated_at DESC)
    WHERE status = 'active';

CREATE TRIGGER trg_document_relations_identity_immutable
BEFORE UPDATE OF
    relation_id,
    relation_type,
    source_document_id,
    target_document_id,
    created_by_actor,
    created_at
ON document_relations
WHEN
    NEW.relation_id <> OLD.relation_id
    OR NEW.relation_type <> OLD.relation_type
    OR NEW.source_document_id <> OLD.source_document_id
    OR NEW.target_document_id <> OLD.target_document_id
    OR NEW.created_by_actor <> OLD.created_by_actor
    OR NEW.created_at <> OLD.created_at
BEGIN
    SELECT RAISE(ABORT, 'document relation identity fields are immutable');
END;

CREATE TRIGGER trg_document_relations_active_only
BEFORE INSERT ON document_relations
WHEN NEW.status <> 'active'
BEGIN
    SELECT RAISE(ABORT, 'document relations must be inserted as active');
END;

CREATE TRIGGER trg_document_relations_retract_once
BEFORE UPDATE ON document_relations
WHEN
    OLD.status = 'retracted'
    OR (
        NEW.status <> OLD.status
        AND NOT (
            OLD.status = 'active'
            AND NEW.status = 'retracted'
        )
    )
BEGIN
    SELECT RAISE(ABORT, 'document relation retraction is terminal');
END;

CREATE TABLE document_relation_suggestions (
    suggestion_id TEXT PRIMARY KEY
        CHECK (length(suggestion_id) = 36),
    suggestion_revision INTEGER NOT NULL
        CHECK (suggestion_revision >= 1),
    payload_version INTEGER NOT NULL DEFAULT 1
        CHECK (payload_version = 1),
    relation_type TEXT NOT NULL
        CHECK (
            relation_type IN (
                'related',
                'example_of',
                'implements',
                'contrasts_with',
                'depends_on'
            )
        ),
    source_document_id TEXT NOT NULL
        CHECK (length(source_document_id) = 36),
    source_document_revision INTEGER NOT NULL
        CHECK (source_document_revision >= 1),
    target_document_id TEXT NOT NULL
        CHECK (length(target_document_id) = 36),
    target_document_revision INTEGER NOT NULL
        CHECK (target_document_revision >= 1),
    reason TEXT NOT NULL
        CHECK (length(trim(reason)) BETWEEN 1 AND 1000),
    model_name TEXT
        CHECK (
            model_name IS NULL
            OR length(model_name) BETWEEN 1 AND 128
        ),
    agent_name TEXT
        CHECK (
            agent_name IS NULL
            OR length(agent_name) BETWEEN 1 AND 128
        ),
    client_name TEXT
        CHECK (
            client_name IS NULL
            OR length(client_name) BETWEEN 1 AND 128
        ),
    evidence_json TEXT NOT NULL DEFAULT '[]'
        CHECK (
            json_valid(evidence_json)
            AND json_type(evidence_json) = 'array'
            AND json_array_length(evidence_json) <= 3
            AND length(CAST(evidence_json AS BLOB)) <= 65536
        ),
    status TEXT NOT NULL DEFAULT 'pending_review'
        CHECK (
            status IN (
                'pending_review',
                'stale',
                'applied',
                'rejected',
                'expired'
            )
        ),
    source_actor TEXT NOT NULL DEFAULT 'mcp'
        CHECK (source_actor = 'mcp'),
    relation_id TEXT
        CHECK (
            relation_id IS NULL
            OR length(relation_id) = 36
        ),
    apply_result TEXT
        CHECK (
            apply_result IS NULL
            OR apply_result IN ('inserted', 'already_exists')
        ),
    stale_reason_code TEXT
        CHECK (
            stale_reason_code IS NULL
            OR stale_reason_code IN (
                'source_revision_changed',
                'target_revision_changed',
                'source_and_target_revision_changed',
                'source_trashed',
                'target_trashed',
                'source_and_target_trashed',
                'source_missing',
                'target_missing'
            )
        ),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    terminal_at TEXT,
    applied_at TEXT,
    CHECK (source_document_id <> target_document_id),
    CHECK (
        model_name IS NOT NULL
        OR agent_name IS NOT NULL
        OR client_name IS NOT NULL
    ),
    CHECK (
        length(CAST(reason AS BLOB))
        + coalesce(length(CAST(model_name AS BLOB)), 0)
        + coalesce(length(CAST(agent_name AS BLOB)), 0)
        + coalesce(length(CAST(client_name AS BLOB)), 0)
        + length(CAST(evidence_json AS BLOB))
        <= 65536
    ),
    CHECK (updated_at >= created_at),
    CHECK (
        (
            status IN ('applied', 'rejected', 'expired')
            AND terminal_at IS NOT NULL
            AND terminal_at >= created_at
            AND terminal_at <= updated_at
        )
        OR
        (
            status IN ('pending_review', 'stale')
            AND terminal_at IS NULL
        )
    ),
    CHECK (
        (
            status = 'applied'
            AND relation_id IS NOT NULL
            AND apply_result IS NOT NULL
            AND applied_at IS NOT NULL
            AND applied_at = terminal_at
        )
        OR
        (
            status <> 'applied'
            AND relation_id IS NULL
            AND apply_result IS NULL
            AND applied_at IS NULL
        )
    ),
    CHECK (
        (
            status = 'stale'
            AND stale_reason_code IS NOT NULL
        )
        OR
        (
            status <> 'stale'
            AND stale_reason_code IS NULL
        )
    ),
    FOREIGN KEY (source_document_id)
        REFERENCES documents(document_id)
        ON DELETE CASCADE,
    FOREIGN KEY (target_document_id)
        REFERENCES documents(document_id)
        ON DELETE CASCADE,
    FOREIGN KEY (relation_id)
        REFERENCES document_relations(relation_id)
        ON DELETE CASCADE
) STRICT;

CREATE UNIQUE INDEX ux_relation_suggestions_pending_edge
    ON document_relation_suggestions(
        relation_type,
        CASE
            WHEN relation_type IN ('related', 'contrasts_with')
             AND source_document_id > target_document_id
            THEN target_document_id
            ELSE source_document_id
        END,
        CASE
            WHEN relation_type IN ('related', 'contrasts_with')
             AND source_document_id > target_document_id
            THEN source_document_id
            ELSE target_document_id
        END
    )
    WHERE status = 'pending_review';

CREATE INDEX ix_relation_suggestions_status
    ON document_relation_suggestions(status, updated_at DESC, suggestion_id);

CREATE INDEX ix_relation_suggestions_source
    ON document_relation_suggestions(
        source_document_id,
        status,
        updated_at DESC
    );

CREATE INDEX ix_relation_suggestions_target
    ON document_relation_suggestions(
        target_document_id,
        status,
        updated_at DESC
    );

CREATE INDEX ix_relation_suggestions_terminal
    ON document_relation_suggestions(terminal_at, suggestion_id)
    WHERE terminal_at IS NOT NULL;

CREATE TRIGGER trg_relation_suggestions_insert_pending
BEFORE INSERT ON document_relation_suggestions
WHEN
    NEW.suggestion_revision <> 1
    OR NEW.status <> 'pending_review'
BEGIN
    SELECT RAISE(ABORT, 'relation suggestions must start pending at revision 1');
END;

CREATE TRIGGER trg_relation_suggestions_revision
BEFORE UPDATE ON document_relation_suggestions
WHEN NEW.suggestion_revision <> OLD.suggestion_revision + 1
BEGIN
    SELECT RAISE(ABORT, 'relation suggestion revision must increment by one');
END;

CREATE TRIGGER trg_relation_suggestions_identity_immutable
BEFORE UPDATE OF
    suggestion_id,
    payload_version,
    relation_type,
    source_document_id,
    target_document_id,
    source_actor,
    created_at
ON document_relation_suggestions
WHEN
    NEW.suggestion_id <> OLD.suggestion_id
    OR NEW.payload_version <> OLD.payload_version
    OR NEW.relation_type <> OLD.relation_type
    OR NEW.source_document_id <> OLD.source_document_id
    OR NEW.target_document_id <> OLD.target_document_id
    OR NEW.source_actor <> OLD.source_actor
    OR NEW.created_at <> OLD.created_at
BEGIN
    SELECT RAISE(ABORT, 'relation suggestion identity fields are immutable');
END;

CREATE TRIGGER trg_relation_suggestions_terminal_immutable
BEFORE UPDATE ON document_relation_suggestions
WHEN OLD.status IN ('applied', 'rejected', 'expired')
BEGIN
    SELECT RAISE(ABORT, 'terminal relation suggestions are immutable');
END;

CREATE TRIGGER trg_relation_suggestions_transition
BEFORE UPDATE OF status ON document_relation_suggestions
WHEN
    NEW.status <> OLD.status
    AND NOT (
        (
            OLD.status = 'pending_review'
            AND NEW.status IN ('applied', 'rejected', 'stale', 'expired')
        )
        OR
        (
            OLD.status = 'stale'
            AND NEW.status = 'pending_review'
        )
    )
BEGIN
    SELECT RAISE(ABORT, 'relation suggestion status transition is invalid');
END;

CREATE TABLE idempotency_records_new (
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
                'mcp_write_mode.change',
                'semantic_index.rebuild',
                'semantic_remote_consent.change',
                'relation_suggestion.create',
                'relation_suggestion.update',
                'relation_suggestion.discard',
                'relation_suggestion.apply',
                'relation_suggestion.reject',
                'document_relation.retract'
            )
        ),
    idempotency_key TEXT NOT NULL
        CHECK (length(idempotency_key) = 36),
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
    PRIMARY KEY (actor_kind, operation, idempotency_key),
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

INSERT INTO idempotency_records_new(
    actor_kind,
    operation,
    idempotency_key,
    session_id,
    request_hash,
    result_json,
    created_at,
    expires_at
)
SELECT
    actor_kind,
    operation,
    idempotency_key,
    session_id,
    request_hash,
    result_json,
    created_at,
    expires_at
FROM idempotency_records;

CREATE TEMP TABLE relation_idempotency_migration_guard (
    value INTEGER NOT NULL CHECK (value = 0)
) STRICT;

INSERT INTO relation_idempotency_migration_guard(value)
SELECT
    (SELECT count(*) FROM idempotency_records_new)
    - (SELECT count(*) FROM idempotency_records);

DROP TABLE relation_idempotency_migration_guard;
DROP TABLE idempotency_records;
ALTER TABLE idempotency_records_new RENAME TO idempotency_records;

CREATE INDEX ix_idempotency_expiry
    ON idempotency_records(
        expires_at,
        actor_kind,
        operation,
        idempotency_key
    );

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
            'draft.cleanup',
            'embedding.remote_consent',
            'embedding.profile_change',
            'embedding.rebuild',
            'relation_suggestion.create',
            'relation_suggestion.update',
            'relation_suggestion.discard',
            'relation_suggestion.apply',
            'relation_suggestion.reject',
            'relation_suggestion.stale',
            'relation_suggestion.expire',
            'relation_suggestion.cleanup',
            'document_relation.retract'
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

CREATE TEMP TABLE relation_audit_migration_guard (
    value INTEGER NOT NULL CHECK (value = 0)
) STRICT;

INSERT INTO relation_audit_migration_guard(value)
SELECT
    (SELECT count(*) FROM audit_events_new)
    - (SELECT count(*) FROM audit_events);

DROP TABLE relation_audit_migration_guard;
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

CREATE TEMP TABLE relation_suggestions_fk_guard (
    value INTEGER NOT NULL CHECK (value = 0)
) STRICT;

INSERT INTO relation_suggestions_fk_guard(value)
SELECT count(*) FROM pragma_foreign_key_check;

DROP TABLE relation_suggestions_fk_guard;

CREATE TEMP TABLE relation_suggestions_integrity_guard (
    value INTEGER NOT NULL CHECK (value = 0)
) STRICT;

INSERT INTO relation_suggestions_integrity_guard(value)
SELECT count(*)
FROM pragma_integrity_check
WHERE integrity_check <> 'ok';

DROP TABLE relation_suggestions_integrity_guard;
