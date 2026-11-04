CREATE TABLE semantic_profiles (
    profile_id TEXT PRIMARY KEY
        CHECK (
            length(profile_id) = 64
            AND profile_id NOT GLOB '*[^0-9a-f]*'
        ),
    provider_id TEXT NOT NULL
        CHECK (length(provider_id) BETWEEN 1 AND 128),
    model_id TEXT NOT NULL
        CHECK (length(model_id) BETWEEN 1 AND 256),
    dimension INTEGER NOT NULL
        CHECK (dimension BETWEEN 1 AND 65536),
    max_input_tokens INTEGER NOT NULL
        CHECK (max_input_tokens BETWEEN 1 AND 1048576),
    profile_version INTEGER NOT NULL
        CHECK (profile_version >= 1),
    chunker_version INTEGER NOT NULL
        CHECK (chunker_version >= 1),
    endpoint_fingerprint TEXT NOT NULL
        CHECK (
            length(endpoint_fingerprint) = 64
            AND endpoint_fingerprint NOT GLOB '*[^0-9a-f]*'
        ),
    is_remote INTEGER NOT NULL
        CHECK (is_remote IN (0, 1)),
    is_active INTEGER NOT NULL DEFAULT 0
        CHECK (is_active IN (0, 1)),
    status TEXT NOT NULL
        CHECK (
            status IN (
                'disabled',
                'building',
                'ready',
                'degraded',
                'failed',
                'invalid'
            )
        ),
    last_error_code TEXT
        CHECK (
            last_error_code IS NULL
            OR length(last_error_code) BETWEEN 1 AND 128
        ),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    ready_at TEXT,
    CHECK (updated_at >= created_at),
    CHECK (ready_at IS NULL OR ready_at >= created_at),
    CHECK (is_active = 1 OR status IN ('invalid', 'disabled', 'failed'))
) STRICT;

CREATE UNIQUE INDEX ux_semantic_profiles_single_active
    ON semantic_profiles(is_active)
    WHERE is_active = 1;

CREATE INDEX ix_semantic_profiles_status
    ON semantic_profiles(status, updated_at DESC);

CREATE TRIGGER trg_semantic_profiles_identity_immutable
BEFORE UPDATE OF
    profile_id,
    provider_id,
    model_id,
    dimension,
    max_input_tokens,
    profile_version,
    chunker_version,
    endpoint_fingerprint,
    is_remote,
    created_at
ON semantic_profiles
WHEN
    NEW.profile_id <> OLD.profile_id
    OR NEW.provider_id <> OLD.provider_id
    OR NEW.model_id <> OLD.model_id
    OR NEW.dimension <> OLD.dimension
    OR NEW.max_input_tokens <> OLD.max_input_tokens
    OR NEW.profile_version <> OLD.profile_version
    OR NEW.chunker_version <> OLD.chunker_version
    OR NEW.endpoint_fingerprint <> OLD.endpoint_fingerprint
    OR NEW.is_remote <> OLD.is_remote
    OR NEW.created_at <> OLD.created_at
BEGIN
    SELECT RAISE(ABORT, 'semantic profile identity fields are immutable');
END;

CREATE TABLE semantic_remote_consents (
    profile_id TEXT PRIMARY KEY,
    consent_version INTEGER NOT NULL
        CHECK (consent_version >= 1),
    endpoint_fingerprint TEXT NOT NULL
        CHECK (
            length(endpoint_fingerprint) = 64
            AND endpoint_fingerprint NOT GLOB '*[^0-9a-f]*'
        ),
    granted_by_actor TEXT NOT NULL
        CHECK (granted_by_actor = 'user_ui'),
    granted_at TEXT NOT NULL,
    revoked_at TEXT,
    updated_at TEXT NOT NULL,
    CHECK (updated_at >= granted_at),
    CHECK (revoked_at IS NULL OR revoked_at >= granted_at),
    FOREIGN KEY (profile_id)
        REFERENCES semantic_profiles(profile_id)
        ON DELETE CASCADE
) STRICT;

CREATE TABLE semantic_index_jobs (
    document_id TEXT NOT NULL
        CHECK (length(document_id) = 36),
    profile_id TEXT NOT NULL,
    job_type TEXT NOT NULL
        CHECK (job_type IN ('index', 'delete', 'purge')),
    status TEXT NOT NULL
        CHECK (
            status IN (
                'pending',
                'running',
                'completed',
                'failed',
                'cancelled',
                'superseded'
            )
        ),
    requested_revision INTEGER NOT NULL
        CHECK (requested_revision >= 1),
    document_content_hash TEXT
        CHECK (
            document_content_hash IS NULL
            OR (
                length(document_content_hash) = 64
                AND document_content_hash NOT GLOB '*[^0-9a-f]*'
            )
        ),
    attempt_count INTEGER NOT NULL DEFAULT 0
        CHECK (attempt_count BETWEEN 0 AND 20),
    available_at TEXT NOT NULL,
    last_error_code TEXT
        CHECK (
            last_error_code IS NULL
            OR length(last_error_code) BETWEEN 1 AND 128
        ),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    completed_at TEXT,
    PRIMARY KEY (document_id, profile_id),
    CHECK (updated_at >= created_at),
    CHECK (available_at >= created_at),
    CHECK (completed_at IS NULL OR completed_at >= created_at),
    CHECK (
        (status = 'completed' AND completed_at IS NOT NULL)
        OR (status <> 'completed' AND completed_at IS NULL)
    ),
    CHECK (
        job_type IN ('index', 'delete', 'purge')
    ),
    FOREIGN KEY (profile_id)
        REFERENCES semantic_profiles(profile_id)
        ON DELETE CASCADE
) STRICT;

CREATE INDEX ix_semantic_index_jobs_due
    ON semantic_index_jobs(status, available_at, updated_at);

CREATE TABLE semantic_chunks (
    document_id TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    source_revision INTEGER NOT NULL
        CHECK (source_revision >= 1),
    chunk_ordinal INTEGER NOT NULL
        CHECK (chunk_ordinal >= 0),
    heading_path_json TEXT NOT NULL DEFAULT '[]'
        CHECK (
            json_valid(heading_path_json)
            AND json_type(heading_path_json) = 'array'
            AND json_array_length(heading_path_json) <= 64
            AND length(heading_path_json) <= 4096
        ),
    start_offset INTEGER NOT NULL
        CHECK (start_offset >= 0),
    end_offset INTEGER NOT NULL
        CHECK (end_offset > start_offset),
    chunk_content_hash TEXT NOT NULL
        CHECK (
            length(chunk_content_hash) = 64
            AND chunk_content_hash NOT GLOB '*[^0-9a-f]*'
        ),
    dimension INTEGER NOT NULL
        CHECK (dimension BETWEEN 1 AND 65536),
    vector_blob BLOB NOT NULL
        CHECK (
            length(vector_blob) = dimension * 4
            AND length(vector_blob) BETWEEN 4 AND 262144
        ),
    created_at TEXT NOT NULL,
    PRIMARY KEY (document_id, profile_id, chunk_ordinal),
    FOREIGN KEY (document_id)
        REFERENCES documents(document_id)
        ON DELETE CASCADE,
    FOREIGN KEY (profile_id)
        REFERENCES semantic_profiles(profile_id)
        ON DELETE CASCADE
) STRICT;

CREATE INDEX ix_semantic_chunks_profile_document
    ON semantic_chunks(profile_id, document_id);

CREATE TRIGGER trg_semantic_chunks_profile_dimension
BEFORE INSERT ON semantic_chunks
WHEN (
    SELECT dimension
    FROM semantic_profiles
    WHERE profile_id = NEW.profile_id
) <> NEW.dimension
BEGIN
    SELECT RAISE(ABORT, 'semantic chunk dimension must match profile');
END;

CREATE TRIGGER trg_semantic_chunks_profile_dimension_update
BEFORE UPDATE OF profile_id, dimension ON semantic_chunks
WHEN (
    SELECT dimension
    FROM semantic_profiles
    WHERE profile_id = NEW.profile_id
) <> NEW.dimension
BEGIN
    SELECT RAISE(ABORT, 'semantic chunk dimension must match profile');
END;

CREATE TABLE idempotency_records_new (
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
                'mcp_write_mode.change',
                'semantic_index.rebuild',
                'semantic_remote_consent.change'
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

INSERT INTO idempotency_records_new(
    idempotency_key,
    actor_kind,
    operation,
    session_id,
    request_hash,
    result_json,
    created_at,
    expires_at
)
SELECT
    idempotency_key,
    actor_kind,
    operation,
    session_id,
    request_hash,
    result_json,
    created_at,
    expires_at
FROM idempotency_records;

CREATE TEMP TABLE idempotency_migration_guard (
    value INTEGER NOT NULL CHECK (value = 0)
) STRICT;

INSERT INTO idempotency_migration_guard(value)
SELECT
    (SELECT count(*) FROM idempotency_records_new)
    - (SELECT count(*) FROM idempotency_records);

DROP TABLE idempotency_migration_guard;
DROP TABLE idempotency_records;
ALTER TABLE idempotency_records_new RENAME TO idempotency_records;

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
            'draft.cleanup',
            'embedding.remote_consent',
            'embedding.profile_change',
            'embedding.rebuild'
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

CREATE TEMP TABLE semantic_index_fk_guard (
    value INTEGER NOT NULL CHECK (value = 0)
) STRICT;

INSERT INTO semantic_index_fk_guard(value)
SELECT count(*) FROM pragma_foreign_key_check;

DROP TABLE semantic_index_fk_guard;

CREATE TEMP TABLE semantic_index_integrity_guard (
    value INTEGER NOT NULL CHECK (value = 0)
) STRICT;

INSERT INTO semantic_index_integrity_guard(value)
SELECT count(*)
FROM pragma_integrity_check
WHERE integrity_check <> 'ok';

DROP TABLE semantic_index_integrity_guard;
