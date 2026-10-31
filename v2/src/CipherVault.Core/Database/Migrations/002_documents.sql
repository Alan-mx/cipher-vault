CREATE TABLE documents (
    document_id TEXT PRIMARY KEY
        CHECK (length(document_id) = 36),
    title TEXT NOT NULL
        CHECK (length(title) BETWEEN 1 AND 200),
    title_key TEXT NOT NULL
        CHECK (length(title_key) > 0),
    path TEXT NOT NULL
        CHECK (length(path) BETWEEN 1 AND 2048),
    path_key TEXT NOT NULL
        CHECK (path_key = path),
    format TEXT NOT NULL
        CHECK (format IN ('md', 'txt')),
    content TEXT NOT NULL,
    frontmatter_json TEXT NOT NULL DEFAULT '{}'
        CHECK (
            json_valid(frontmatter_json)
            AND json_type(frontmatter_json) = 'object'
        ),
    headings_json TEXT NOT NULL DEFAULT '[]'
        CHECK (
            json_valid(headings_json)
            AND json_type(headings_json) = 'array'
        ),
    revision INTEGER NOT NULL
        CHECK (revision >= 1),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    deleted_at TEXT,
    purge_eligible_at TEXT,
    CHECK (
        (deleted_at IS NULL AND purge_eligible_at IS NULL)
        OR
        (
            deleted_at IS NOT NULL
            AND purge_eligible_at IS NOT NULL
            AND purge_eligible_at >= deleted_at
        )
    )
) STRICT;

CREATE UNIQUE INDEX ux_documents_active_path
    ON documents(path_key)
    WHERE deleted_at IS NULL;

CREATE INDEX ix_documents_active_updated
    ON documents(updated_at DESC, document_id)
    WHERE deleted_at IS NULL;

CREATE INDEX ix_documents_active_title_key
    ON documents(title_key, document_id)
    WHERE deleted_at IS NULL;

CREATE INDEX ix_documents_trash_eligible
    ON documents(purge_eligible_at, document_id)
    WHERE deleted_at IS NOT NULL;

CREATE INDEX ix_documents_format
    ON documents(format, document_id);

CREATE TABLE document_revisions (
    revision_id TEXT PRIMARY KEY
        CHECK (length(revision_id) = 36),
    document_id TEXT NOT NULL,
    revision INTEGER NOT NULL
        CHECK (revision >= 1),
    title TEXT NOT NULL,
    path TEXT NOT NULL,
    format TEXT NOT NULL
        CHECK (format IN ('md', 'txt')),
    content TEXT NOT NULL,
    frontmatter_json TEXT NOT NULL DEFAULT '{}'
        CHECK (
            json_valid(frontmatter_json)
            AND json_type(frontmatter_json) = 'object'
        ),
    headings_json TEXT NOT NULL DEFAULT '[]'
        CHECK (
            json_valid(headings_json)
            AND json_type(headings_json) = 'array'
        ),
    tags_json TEXT NOT NULL DEFAULT '[]'
        CHECK (
            json_valid(tags_json)
            AND json_type(tags_json) = 'array'
        ),
    created_at TEXT NOT NULL,
    retain_until TEXT NOT NULL,
    UNIQUE (document_id, revision),
    CHECK (retain_until >= created_at),
    FOREIGN KEY (document_id)
        REFERENCES documents(document_id)
        ON DELETE CASCADE
) STRICT;

CREATE INDEX ix_document_revisions_document
    ON document_revisions(document_id, revision DESC);

CREATE INDEX ix_document_revisions_retain
    ON document_revisions(retain_until, revision_id);

CREATE TRIGGER trg_document_revisions_no_update
BEFORE UPDATE ON document_revisions
BEGIN
    SELECT RAISE(ABORT, 'document_revisions is append-only');
END;

CREATE TABLE tags (
    tag_id TEXT PRIMARY KEY
        CHECK (length(tag_id) = 36),
    name TEXT NOT NULL
        CHECK (length(name) BETWEEN 1 AND 64),
    normalized_key TEXT NOT NULL UNIQUE
        CHECK (length(normalized_key) BETWEEN 1 AND 192)
) STRICT;

CREATE INDEX ix_tags_name ON tags(name, tag_id);

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
) STRICT;

CREATE INDEX ix_document_tags_tag
    ON document_tags(tag_id, document_id);

CREATE TABLE document_links (
    link_id TEXT PRIMARY KEY
        CHECK (length(link_id) = 36),
    source_document_id TEXT NOT NULL,
    source_revision INTEGER NOT NULL
        CHECK (source_revision >= 1),
    target_document_id TEXT,
    raw_target TEXT NOT NULL
        CHECK (length(raw_target) BETWEEN 1 AND 512),
    raw_target_key TEXT NOT NULL
        CHECK (length(raw_target_key) BETWEEN 1 AND 512),
    display_text TEXT NOT NULL
        CHECK (length(display_text) <= 512),
    target_heading TEXT
        CHECK (target_heading IS NULL OR length(target_heading) <= 256),
    target_heading_key TEXT
        CHECK (target_heading_key IS NULL OR length(target_heading_key) <= 256),
    relation_type TEXT NOT NULL DEFAULT 'references'
        CHECK (relation_type IN ('references')),
    resolution_status TEXT NOT NULL
        CHECK (resolution_status IN ('resolved', 'unresolved', 'ambiguous')),
    candidate_document_ids_json TEXT NOT NULL DEFAULT '[]'
        CHECK (
            json_valid(candidate_document_ids_json)
            AND json_type(candidate_document_ids_json) = 'array'
        ),
    start_offset INTEGER NOT NULL
        CHECK (start_offset >= 0),
    end_offset INTEGER NOT NULL
        CHECK (end_offset > start_offset),
    UNIQUE (source_document_id, start_offset, end_offset),
    CHECK (
        (
            resolution_status = 'resolved'
            AND target_document_id IS NOT NULL
            AND json_array_length(candidate_document_ids_json) = 0
        )
        OR
        (
            resolution_status = 'unresolved'
            AND target_document_id IS NULL
            AND json_array_length(candidate_document_ids_json) = 0
        )
        OR
        (
            resolution_status = 'ambiguous'
            AND target_document_id IS NULL
            AND json_array_length(candidate_document_ids_json) >= 2
        )
    ),
    FOREIGN KEY (source_document_id)
        REFERENCES documents(document_id)
        ON DELETE CASCADE,
    FOREIGN KEY (target_document_id)
        REFERENCES documents(document_id)
        ON DELETE SET NULL
) STRICT;

CREATE INDEX ix_document_links_source
    ON document_links(source_document_id, source_revision, start_offset);

CREATE INDEX ix_document_links_target
    ON document_links(target_document_id, source_document_id)
    WHERE target_document_id IS NOT NULL;

CREATE INDEX ix_document_links_raw_target
    ON document_links(raw_target_key, resolution_status);

CREATE TRIGGER trg_document_links_target_before_delete
BEFORE DELETE ON documents
BEGIN
    UPDATE document_links
    SET target_document_id = NULL,
        resolution_status = 'unresolved',
        candidate_document_ids_json = '[]'
    WHERE target_document_id = OLD.document_id;
END;

CREATE VIRTUAL TABLE document_fts USING fts5(
    document_id UNINDEXED,
    title,
    path,
    headings,
    tags,
    content,
    tokenize = 'trigram'
);

CREATE TABLE audit_events (
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
            'link_rebuild'
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
