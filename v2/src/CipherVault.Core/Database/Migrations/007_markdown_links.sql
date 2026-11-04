DROP TRIGGER IF EXISTS trg_document_links_target_before_delete;

CREATE TABLE document_links_new (
    link_id TEXT PRIMARY KEY
        CHECK (length(link_id) = 36),
    source_document_id TEXT NOT NULL,
    source_revision INTEGER NOT NULL
        CHECK (source_revision >= 1),
    target_document_id TEXT,
    link_kind TEXT NOT NULL
        CHECK (link_kind IN ('wiki', 'markdown')),
    raw_target TEXT NOT NULL
        CHECK (length(raw_target) BETWEEN 1 AND 4096),
    raw_target_key TEXT
        CHECK (
            raw_target_key IS NULL
            OR length(raw_target_key) BETWEEN 1 AND 512
        ),
    canonical_target_key TEXT
        CHECK (
            canonical_target_key IS NULL
            OR length(canonical_target_key) BETWEEN 1 AND 2048
        ),
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
    destination_start_offset INTEGER,
    destination_end_offset INTEGER,
    CHECK (
        (
            link_kind = 'wiki'
            AND raw_target_key IS NOT NULL
            AND canonical_target_key IS NULL
            AND destination_start_offset IS NULL
            AND destination_end_offset IS NULL
        )
        OR
        (
            link_kind = 'markdown'
            AND raw_target_key IS NULL
            AND canonical_target_key IS NOT NULL
            AND destination_start_offset IS NOT NULL
            AND destination_end_offset IS NOT NULL
            AND destination_start_offset > start_offset
            AND destination_end_offset > destination_start_offset
            AND destination_end_offset <= end_offset
        )
    ),
    CHECK (
        (target_heading IS NULL AND target_heading_key IS NULL)
        OR
        (target_heading IS NOT NULL AND target_heading_key IS NOT NULL)
    ),
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

INSERT INTO document_links_new(
    link_id,
    source_document_id,
    source_revision,
    target_document_id,
    link_kind,
    raw_target,
    raw_target_key,
    canonical_target_key,
    display_text,
    target_heading,
    target_heading_key,
    relation_type,
    resolution_status,
    candidate_document_ids_json,
    start_offset,
    end_offset,
    destination_start_offset,
    destination_end_offset
)
SELECT
    link_id,
    source_document_id,
    source_revision,
    target_document_id,
    'wiki',
    raw_target,
    raw_target_key,
    NULL,
    display_text,
    target_heading,
    target_heading_key,
    relation_type,
    resolution_status,
    candidate_document_ids_json,
    start_offset,
    end_offset,
    NULL,
    NULL
FROM document_links;

CREATE TEMP TABLE document_links_migration_guard (
    value INTEGER NOT NULL CHECK (value = 0)
) STRICT;

INSERT INTO document_links_migration_guard(value)
SELECT
    (SELECT count(*) FROM document_links_new)
    - (SELECT count(*) FROM document_links);

DROP TABLE document_links_migration_guard;
DROP TABLE document_links;
ALTER TABLE document_links_new RENAME TO document_links;

CREATE UNIQUE INDEX ux_document_links_source_position
    ON document_links(source_document_id, start_offset);

CREATE INDEX ix_document_links_source
    ON document_links(source_document_id, source_revision, start_offset);

CREATE INDEX ix_document_links_target
    ON document_links(target_document_id, source_document_id)
    WHERE target_document_id IS NOT NULL;

CREATE INDEX ix_document_links_wiki_target
    ON document_links(raw_target_key, resolution_status)
    WHERE link_kind = 'wiki';

CREATE INDEX ix_document_links_markdown_target
    ON document_links(canonical_target_key, resolution_status)
    WHERE link_kind = 'markdown';

CREATE TRIGGER trg_document_links_target_before_delete
BEFORE DELETE ON documents
BEGIN
    UPDATE document_links
    SET target_document_id = NULL,
        resolution_status = 'unresolved',
        candidate_document_ids_json = '[]'
    WHERE target_document_id = OLD.document_id;
END;

CREATE TEMP TABLE document_links_fk_guard (
    value INTEGER NOT NULL CHECK (value = 0)
) STRICT;

INSERT INTO document_links_fk_guard(value)
SELECT count(*) FROM pragma_foreign_key_check;

DROP TABLE document_links_fk_guard;

CREATE TEMP TABLE document_links_integrity_guard (
    value INTEGER NOT NULL CHECK (value = 0)
) STRICT;

INSERT INTO document_links_integrity_guard(value)
SELECT count(*)
FROM pragma_integrity_check
WHERE integrity_check <> 'ok';

DROP TABLE document_links_integrity_guard;
