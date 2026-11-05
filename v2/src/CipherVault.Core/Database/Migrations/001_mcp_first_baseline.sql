CREATE TABLE vault_meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
) STRICT;

INSERT INTO vault_meta(key, value)
VALUES
    ('vault_id', $vaultId),
    ('schema_version', '0'),
    ('created_at', $createdAtUtc);

CREATE TABLE knowledge_model_state (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    version INTEGER NOT NULL CHECK (version >= 1),
    updated_at TEXT NOT NULL
) STRICT;

INSERT INTO knowledge_model_state(id, version, updated_at)
VALUES (1, 1, $createdAtUtc);

CREATE TABLE knowledge_types (
    type_id TEXT PRIMARY KEY
        CHECK (length(type_id) = 36),
    type_key TEXT NOT NULL UNIQUE
        CHECK (length(type_key) BETWEEN 1 AND 64),
    name TEXT NOT NULL
        CHECK (length(name) BETWEEN 1 AND 100),
    description TEXT
        CHECK (description IS NULL OR length(description) <= 500),
    is_enabled INTEGER NOT NULL DEFAULT 1
        CHECK (is_enabled IN (0, 1)),
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    CHECK (updated_at >= created_at)
) STRICT;

CREATE INDEX ix_knowledge_types_enabled_order
    ON knowledge_types(is_enabled, sort_order, type_id);

CREATE TABLE knowledge_property_definitions (
    property_id TEXT PRIMARY KEY
        CHECK (length(property_id) = 36),
    knowledge_type_id TEXT NOT NULL,
    property_key TEXT NOT NULL
        CHECK (length(property_key) BETWEEN 1 AND 64),
    name TEXT NOT NULL
        CHECK (length(name) BETWEEN 1 AND 100),
    value_type TEXT NOT NULL
        CHECK (value_type IN (
            'string', 'number', 'boolean', 'date', 'enum', 'array', 'object'
        )),
    is_required INTEGER NOT NULL DEFAULT 0
        CHECK (is_required IN (0, 1)),
    default_value_json TEXT
        CHECK (
            default_value_json IS NULL
            OR (
                json_valid(default_value_json)
                AND length(CAST(default_value_json AS BLOB)) <= 65536
            )
        ),
    constraints_json TEXT NOT NULL DEFAULT '{}'
        CHECK (
            json_valid(constraints_json)
            AND json_type(constraints_json) = 'object'
            AND length(CAST(constraints_json AS BLOB)) <= 65536
        ),
    is_enabled INTEGER NOT NULL DEFAULT 1
        CHECK (is_enabled IN (0, 1)),
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (knowledge_type_id, property_key),
    CHECK (updated_at >= created_at),
    FOREIGN KEY (knowledge_type_id)
        REFERENCES knowledge_types(type_id)
        ON DELETE RESTRICT
) STRICT;

CREATE INDEX ix_property_type_order
    ON knowledge_property_definitions(
        knowledge_type_id, is_enabled, sort_order, property_id);

CREATE TABLE knowledge_templates (
    template_id TEXT PRIMARY KEY
        CHECK (length(template_id) = 36),
    knowledge_type_id TEXT NOT NULL,
    template_key TEXT NOT NULL
        CHECK (length(template_key) BETWEEN 1 AND 64),
    name TEXT NOT NULL
        CHECK (length(name) BETWEEN 1 AND 100),
    description TEXT
        CHECK (description IS NULL OR length(description) <= 500),
    body_template TEXT NOT NULL
        CHECK (length(CAST(body_template AS BLOB)) <= 1048576),
    fields_json TEXT NOT NULL DEFAULT '[]'
        CHECK (
            json_valid(fields_json)
            AND json_type(fields_json) = 'array'
            AND length(CAST(fields_json AS BLOB)) <= 65536
        ),
    is_enabled INTEGER NOT NULL DEFAULT 1
        CHECK (is_enabled IN (0, 1)),
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (knowledge_type_id, template_key),
    CHECK (updated_at >= created_at),
    FOREIGN KEY (knowledge_type_id)
        REFERENCES knowledge_types(type_id)
        ON DELETE RESTRICT
) STRICT;

CREATE INDEX ix_templates_type_order
    ON knowledge_templates(
        knowledge_type_id, is_enabled, sort_order, template_id);

CREATE TABLE relation_types (
    relation_type_id TEXT PRIMARY KEY
        CHECK (length(relation_type_id) = 36),
    relation_key TEXT NOT NULL UNIQUE
        CHECK (length(relation_key) BETWEEN 1 AND 64),
    name TEXT NOT NULL
        CHECK (length(name) BETWEEN 1 AND 100),
    description TEXT
        CHECK (description IS NULL OR length(description) <= 500),
    is_directed INTEGER NOT NULL
        CHECK (is_directed IN (0, 1)),
    is_enabled INTEGER NOT NULL DEFAULT 1
        CHECK (is_enabled IN (0, 1)),
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    CHECK (updated_at >= created_at)
) STRICT;

CREATE INDEX ix_relation_types_enabled_order
    ON relation_types(is_enabled, sort_order, relation_type_id);

CREATE TABLE source_rules (
    source_rule_id TEXT PRIMARY KEY
        CHECK (length(source_rule_id) = 36),
    source_kind TEXT NOT NULL UNIQUE
        CHECK (source_kind IN (
            'web_page', 'local_file', 'book', 'podcast', 'other'
        )),
    metadata_schema_json TEXT NOT NULL DEFAULT '{}'
        CHECK (
            json_valid(metadata_schema_json)
            AND json_type(metadata_schema_json) = 'object'
            AND length(CAST(metadata_schema_json AS BLOB)) <= 65536
        ),
    supported_locator_types_json TEXT NOT NULL
        CHECK (
            json_valid(supported_locator_types_json)
            AND json_type(supported_locator_types_json) = 'array'
            AND length(CAST(supported_locator_types_json AS BLOB)) <= 4096
        ),
    is_enabled INTEGER NOT NULL DEFAULT 1
        CHECK (is_enabled IN (0, 1)),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    CHECK (updated_at >= created_at)
) STRICT;

CREATE TABLE workflow_definitions (
    workflow_id TEXT PRIMARY KEY
        CHECK (length(workflow_id) = 36),
    workflow_key TEXT NOT NULL UNIQUE
        CHECK (length(workflow_key) BETWEEN 1 AND 64),
    name TEXT NOT NULL
        CHECK (length(name) BETWEEN 1 AND 100),
    description TEXT
        CHECK (description IS NULL OR length(description) <= 500),
    target_object_types_json TEXT NOT NULL
        CHECK (
            json_valid(target_object_types_json)
            AND json_type(target_object_types_json) = 'array'
            AND length(CAST(target_object_types_json AS BLOB)) <= 4096
        ),
    constraints_json TEXT NOT NULL DEFAULT '{}'
        CHECK (
            json_valid(constraints_json)
            AND json_type(constraints_json) = 'object'
            AND length(CAST(constraints_json AS BLOB)) <= 65536
        ),
    is_enabled INTEGER NOT NULL DEFAULT 1
        CHECK (is_enabled IN (0, 1)),
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    CHECK (updated_at >= created_at)
) STRICT;

CREATE TABLE sources (
    source_id TEXT PRIMARY KEY
        CHECK (length(source_id) = 36),
    source_kind TEXT NOT NULL
        CHECK (source_kind IN (
            'web_page', 'local_file', 'book', 'podcast', 'other'
        )),
    title TEXT NOT NULL
        CHECK (length(title) BETWEEN 1 AND 200),
    title_key TEXT NOT NULL
        CHECK (length(title_key) BETWEEN 1 AND 200),
    metadata_json TEXT NOT NULL DEFAULT '{}'
        CHECK (
            json_valid(metadata_json)
            AND json_type(metadata_json) = 'object'
            AND length(CAST(metadata_json AS BLOB)) <= 65536
        ),
    status TEXT NOT NULL DEFAULT 'active'
        CHECK (status IN ('active', 'archived')),
    current_revision_id TEXT NOT NULL
        CHECK (length(current_revision_id) = 36),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    archived_at TEXT,
    CHECK (updated_at >= created_at),
    CHECK (
        (
            status = 'active'
            AND archived_at IS NULL
        )
        OR
        (
            status = 'archived'
            AND archived_at IS NOT NULL
            AND archived_at >= created_at
            AND archived_at <= updated_at
        )
    ),
    FOREIGN KEY (source_id, current_revision_id)
        REFERENCES source_revisions(source_id, source_revision_id)
        ON DELETE RESTRICT
        DEFERRABLE INITIALLY DEFERRED
) STRICT;

CREATE INDEX ix_sources_status_updated
    ON sources(status, updated_at DESC, source_id);

CREATE INDEX ix_sources_kind_status
    ON sources(source_kind, status, source_id);

CREATE INDEX ix_sources_title_key
    ON sources(title_key, source_id);

CREATE TABLE source_revisions (
    source_revision_id TEXT PRIMARY KEY
        CHECK (length(source_revision_id) = 36),
    source_id TEXT NOT NULL,
    revision INTEGER NOT NULL
        CHECK (revision >= 1),
    source_kind TEXT NOT NULL
        CHECK (source_kind IN (
            'web_page', 'local_file', 'book', 'podcast', 'other'
        )),
    title TEXT NOT NULL
        CHECK (length(title) BETWEEN 1 AND 200),
    metadata_json TEXT NOT NULL DEFAULT '{}'
        CHECK (
            json_valid(metadata_json)
            AND json_type(metadata_json) = 'object'
            AND length(CAST(metadata_json AS BLOB)) <= 65536
        ),
    content_format TEXT NOT NULL
        CHECK (content_format IN ('markdown', 'text')),
    content_text TEXT NOT NULL
        CHECK (length(CAST(content_text AS BLOB)) <= 4194304),
    content_hash TEXT NOT NULL
        CHECK (
            length(content_hash) = 64
            AND content_hash NOT GLOB '*[^0-9a-f]*'
        ),
    knowledge_model_version INTEGER NOT NULL
        CHECK (knowledge_model_version >= 1),
    locator_index_version INTEGER NOT NULL DEFAULT 1
        CHECK (locator_index_version >= 1),
    created_at TEXT NOT NULL,
    UNIQUE (source_id, revision),
    UNIQUE (source_id, source_revision_id),
    UNIQUE (source_revision_id, source_id, revision),
    FOREIGN KEY (source_id)
        REFERENCES sources(source_id)
        ON DELETE RESTRICT,
    FOREIGN KEY (source_kind)
        REFERENCES source_rules(source_kind)
        ON DELETE RESTRICT
) STRICT;

CREATE INDEX ix_source_revisions_source
    ON source_revisions(source_id, revision DESC);

CREATE TABLE source_locators (
    source_locator_id TEXT PRIMARY KEY
        CHECK (length(source_locator_id) = 36),
    source_revision_id TEXT NOT NULL,
    locator_type TEXT NOT NULL
        CHECK (locator_type IN (
            'section', 'paragraph', 'page', 'timestamp'
        )),
    locator_key TEXT NOT NULL
        CHECK (length(locator_key) BETWEEN 1 AND 1024),
    locator_value_json TEXT NOT NULL
        CHECK (
            json_valid(locator_value_json)
            AND length(CAST(locator_value_json AS BLOB)) <= 8192
        ),
    label TEXT
        CHECK (label IS NULL OR length(label) <= 512),
    start_offset INTEGER
        CHECK (start_offset IS NULL OR start_offset >= 0),
    end_offset INTEGER
        CHECK (
            end_offset IS NULL
            OR (start_offset IS NOT NULL AND end_offset > start_offset)
        ),
    ordinal INTEGER NOT NULL
        CHECK (ordinal >= 0),
    parent_locator_id TEXT
        CHECK (parent_locator_id IS NULL OR length(parent_locator_id) = 36),
    UNIQUE (source_revision_id, locator_type, locator_key),
    UNIQUE (source_locator_id, source_revision_id),
    UNIQUE (
        source_locator_id,
        source_revision_id,
        locator_type,
        locator_key,
        locator_value_json
    ),
    CHECK (
        (
            locator_type IN ('section', 'paragraph')
            AND start_offset IS NOT NULL
            AND end_offset IS NOT NULL
        )
        OR
        locator_type IN ('page', 'timestamp')
    ),
    FOREIGN KEY (source_revision_id)
        REFERENCES source_revisions(source_revision_id)
        ON DELETE RESTRICT,
    FOREIGN KEY (parent_locator_id, source_revision_id)
        REFERENCES source_locators(source_locator_id, source_revision_id)
        ON DELETE RESTRICT
) STRICT;

CREATE INDEX ix_source_locators_revision
    ON source_locators(source_revision_id, locator_type, ordinal);

CREATE TABLE knowledge_items (
    knowledge_id TEXT PRIMARY KEY
        CHECK (length(knowledge_id) = 36),
    title TEXT NOT NULL
        CHECK (length(title) BETWEEN 1 AND 200),
    title_key TEXT NOT NULL
        CHECK (length(title_key) BETWEEN 1 AND 200),
    summary TEXT NOT NULL DEFAULT ''
        CHECK (length(summary) <= 4000),
    knowledge_type_id TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'active'
        CHECK (status IN ('active', 'archived')),
    current_revision_id TEXT NOT NULL
        CHECK (length(current_revision_id) = 36),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    archived_at TEXT,
    CHECK (updated_at >= created_at),
    CHECK (
        (
            status = 'active'
            AND archived_at IS NULL
        )
        OR
        (
            status = 'archived'
            AND archived_at IS NOT NULL
            AND archived_at >= created_at
            AND archived_at <= updated_at
        )
    ),
    FOREIGN KEY (knowledge_type_id)
        REFERENCES knowledge_types(type_id)
        ON DELETE RESTRICT,
    FOREIGN KEY (knowledge_id, current_revision_id)
        REFERENCES knowledge_revisions(knowledge_id, knowledge_revision_id)
        ON DELETE RESTRICT
        DEFERRABLE INITIALLY DEFERRED
) STRICT;

CREATE INDEX ix_knowledge_status_updated
    ON knowledge_items(status, updated_at DESC, knowledge_id);

CREATE INDEX ix_knowledge_type_status
    ON knowledge_items(knowledge_type_id, status, knowledge_id);

CREATE INDEX ix_knowledge_title_key
    ON knowledge_items(title_key, knowledge_id);

CREATE TABLE knowledge_revisions (
    knowledge_revision_id TEXT PRIMARY KEY
        CHECK (length(knowledge_revision_id) = 36),
    knowledge_id TEXT NOT NULL,
    revision INTEGER NOT NULL
        CHECK (revision >= 1),
    knowledge_type_id TEXT NOT NULL,
    template_id TEXT,
    knowledge_model_version INTEGER NOT NULL
        CHECK (knowledge_model_version >= 1),
    title TEXT NOT NULL
        CHECK (length(title) BETWEEN 1 AND 200),
    summary TEXT NOT NULL DEFAULT ''
        CHECK (length(summary) <= 4000),
    content_markdown TEXT NOT NULL
        CHECK (length(CAST(content_markdown AS BLOB)) <= 4194304),
    content_hash TEXT NOT NULL
        CHECK (
            length(content_hash) = 64
            AND content_hash NOT GLOB '*[^0-9a-f]*'
        ),
    properties_json TEXT NOT NULL DEFAULT '{}'
        CHECK (
            json_valid(properties_json)
            AND json_type(properties_json) = 'object'
            AND length(CAST(properties_json AS BLOB)) <= 262144
        ),
    tags_json TEXT NOT NULL DEFAULT '[]'
        CHECK (
            json_valid(tags_json)
            AND json_type(tags_json) = 'array'
            AND length(CAST(tags_json AS BLOB)) <= 32768
        ),
    created_at TEXT NOT NULL,
    UNIQUE (knowledge_id, revision),
    UNIQUE (knowledge_id, knowledge_revision_id),
    FOREIGN KEY (knowledge_id)
        REFERENCES knowledge_items(knowledge_id)
        ON DELETE RESTRICT,
    FOREIGN KEY (knowledge_type_id)
        REFERENCES knowledge_types(type_id)
        ON DELETE RESTRICT,
    FOREIGN KEY (template_id)
        REFERENCES knowledge_templates(template_id)
        ON DELETE RESTRICT
) STRICT;

CREATE INDEX ix_knowledge_revisions_item
    ON knowledge_revisions(knowledge_id, revision DESC);

CREATE TABLE knowledge_tags (
    knowledge_id TEXT NOT NULL,
    tag_key TEXT NOT NULL
        CHECK (length(tag_key) BETWEEN 1 AND 192),
    tag_label TEXT NOT NULL
        CHECK (length(tag_label) BETWEEN 1 AND 64),
    PRIMARY KEY (knowledge_id, tag_key),
    FOREIGN KEY (knowledge_id)
        REFERENCES knowledge_items(knowledge_id)
        ON DELETE RESTRICT
) STRICT;

CREATE INDEX ix_knowledge_tags_key
    ON knowledge_tags(tag_key, knowledge_id);

CREATE TABLE source_references (
    source_reference_id TEXT PRIMARY KEY
        CHECK (length(source_reference_id) = 36),
    knowledge_revision_id TEXT NOT NULL,
    source_id TEXT NOT NULL,
    source_revision_id TEXT NOT NULL,
    source_revision INTEGER NOT NULL
        CHECK (source_revision >= 1),
    source_locator_id TEXT NOT NULL,
    locator_type TEXT NOT NULL
        CHECK (locator_type IN (
            'section', 'paragraph', 'page', 'timestamp'
        )),
    locator_key TEXT NOT NULL
        CHECK (length(locator_key) BETWEEN 1 AND 1024),
    locator_value_json TEXT NOT NULL
        CHECK (
            json_valid(locator_value_json)
            AND length(CAST(locator_value_json AS BLOB)) <= 8192
        ),
    excerpt TEXT NOT NULL
        CHECK (length(excerpt) BETWEEN 1 AND 4000),
    extraction_metadata_json TEXT NOT NULL DEFAULT '{}'
        CHECK (
            json_valid(extraction_metadata_json)
            AND json_type(extraction_metadata_json) = 'object'
            AND length(CAST(extraction_metadata_json AS BLOB)) <= 16384
        ),
    created_at TEXT NOT NULL,
    FOREIGN KEY (knowledge_revision_id)
        REFERENCES knowledge_revisions(knowledge_revision_id)
        ON DELETE RESTRICT,
    FOREIGN KEY (source_revision_id, source_id, source_revision)
        REFERENCES source_revisions(source_revision_id, source_id, revision)
        ON DELETE RESTRICT,
    FOREIGN KEY (
        source_locator_id,
        source_revision_id,
        locator_type,
        locator_key,
        locator_value_json
    )
        REFERENCES source_locators(
            source_locator_id,
            source_revision_id,
            locator_type,
            locator_key,
            locator_value_json
        )
        ON DELETE RESTRICT
) STRICT;

CREATE INDEX ix_source_references_revision
    ON source_references(knowledge_revision_id, source_reference_id);

CREATE INDEX ix_source_references_source
    ON source_references(source_id, source_revision, knowledge_revision_id);

CREATE TABLE knowledge_relations (
    relation_id TEXT PRIMARY KEY
        CHECK (length(relation_id) = 36),
    relation_type_id TEXT NOT NULL,
    source_knowledge_id TEXT NOT NULL,
    target_knowledge_id TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'active'
        CHECK (status IN ('active', 'retracted')),
    current_revision_id TEXT NOT NULL
        CHECK (length(current_revision_id) = 36),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    CHECK (source_knowledge_id <> target_knowledge_id),
    CHECK (updated_at >= created_at),
    FOREIGN KEY (relation_type_id)
        REFERENCES relation_types(relation_type_id)
        ON DELETE RESTRICT,
    FOREIGN KEY (source_knowledge_id)
        REFERENCES knowledge_items(knowledge_id)
        ON DELETE RESTRICT,
    FOREIGN KEY (target_knowledge_id)
        REFERENCES knowledge_items(knowledge_id)
        ON DELETE RESTRICT,
    FOREIGN KEY (relation_id, current_revision_id)
        REFERENCES knowledge_relation_revisions(relation_id, relation_revision_id)
        ON DELETE RESTRICT
        DEFERRABLE INITIALLY DEFERRED
) STRICT;

CREATE UNIQUE INDEX ux_knowledge_relations_active
    ON knowledge_relations(
        relation_type_id, source_knowledge_id, target_knowledge_id)
    WHERE status = 'active';

CREATE INDEX ix_knowledge_relations_source
    ON knowledge_relations(source_knowledge_id, status, target_knowledge_id);

CREATE INDEX ix_knowledge_relations_target
    ON knowledge_relations(target_knowledge_id, status, source_knowledge_id);

CREATE TABLE knowledge_relation_revisions (
    relation_revision_id TEXT PRIMARY KEY
        CHECK (length(relation_revision_id) = 36),
    relation_id TEXT NOT NULL,
    revision INTEGER NOT NULL
        CHECK (revision >= 1),
    relation_type_id TEXT NOT NULL,
    source_knowledge_id TEXT NOT NULL,
    target_knowledge_id TEXT NOT NULL,
    source_knowledge_revision_id TEXT NOT NULL,
    target_knowledge_revision_id TEXT NOT NULL,
    status TEXT NOT NULL
        CHECK (status IN ('active', 'retracted')),
    rationale TEXT NOT NULL
        CHECK (length(rationale) BETWEEN 1 AND 4000),
    evidence_json TEXT NOT NULL DEFAULT '[]'
        CHECK (
            json_valid(evidence_json)
            AND json_type(evidence_json) = 'array'
            AND length(CAST(evidence_json AS BLOB)) <= 65536
        ),
    knowledge_model_version INTEGER NOT NULL
        CHECK (knowledge_model_version >= 1),
    created_at TEXT NOT NULL,
    UNIQUE (relation_id, revision),
    UNIQUE (relation_id, relation_revision_id),
    UNIQUE (relation_revision_id, relation_id, revision),
    CHECK (source_knowledge_id <> target_knowledge_id),
    FOREIGN KEY (relation_id)
        REFERENCES knowledge_relations(relation_id)
        ON DELETE RESTRICT,
    FOREIGN KEY (relation_type_id)
        REFERENCES relation_types(relation_type_id)
        ON DELETE RESTRICT,
    FOREIGN KEY (source_knowledge_id, source_knowledge_revision_id)
        REFERENCES knowledge_revisions(knowledge_id, knowledge_revision_id)
        ON DELETE RESTRICT,
    FOREIGN KEY (target_knowledge_id, target_knowledge_revision_id)
        REFERENCES knowledge_revisions(knowledge_id, knowledge_revision_id)
        ON DELETE RESTRICT
) STRICT;

CREATE INDEX ix_relation_revisions_relation
    ON knowledge_relation_revisions(relation_id, revision DESC);

CREATE TABLE drafts (
    draft_id TEXT PRIMARY KEY
        CHECK (length(draft_id) = 36),
    draft_revision INTEGER NOT NULL
        CHECK (draft_revision >= 1),
    target_object_type TEXT NOT NULL
        CHECK (target_object_type IN ('knowledge', 'source', 'relation')),
    operation_type TEXT NOT NULL
        CHECK (operation_type IN (
            'create', 'update', 'archive', 'restore', 'retract'
        )),
    target_id TEXT
        CHECK (target_id IS NULL OR length(target_id) = 36),
    base_revision INTEGER
        CHECK (base_revision IS NULL OR base_revision >= 1),
    base_model_version INTEGER NOT NULL
        CHECK (base_model_version >= 1),
    payload_version INTEGER NOT NULL
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
    status TEXT NOT NULL DEFAULT 'draft'
        CHECK (status IN (
            'draft',
            'pending_review',
            'conflict',
            'applied',
            'rejected',
            'discarded',
            'expired'
        )),
    source_actor TEXT NOT NULL
        CHECK (source_actor IN ('user_ui', 'mcp')),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    terminal_at TEXT,
    applied_object_type TEXT
        CHECK (
            applied_object_type IS NULL
            OR applied_object_type IN ('knowledge', 'source', 'relation')
        ),
    applied_object_id TEXT
        CHECK (
            applied_object_id IS NULL
            OR length(applied_object_id) = 36
        ),
    applied_object_revision INTEGER
        CHECK (
            applied_object_revision IS NULL
            OR applied_object_revision >= 1
        ),
    applied_at TEXT,
    CHECK (updated_at >= created_at),
    CHECK (
        (
            target_object_type = 'relation'
            AND operation_type IN ('create', 'update', 'restore', 'retract')
        )
        OR
        (
            target_object_type IN ('knowledge', 'source')
            AND operation_type IN ('create', 'update', 'archive', 'restore')
        )
    ),
    CHECK (
        (
            operation_type = 'create'
            AND target_id IS NULL
            AND base_revision IS NULL
            AND base_snapshot_json IS NULL
        )
        OR
        (
            operation_type <> 'create'
            AND target_id IS NOT NULL
            AND base_revision IS NOT NULL
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
            AND applied_object_type = target_object_type
            AND applied_object_id IS NOT NULL
            AND applied_object_revision IS NOT NULL
            AND applied_at IS NOT NULL
            AND applied_at = terminal_at
        )
        OR
        (
            status <> 'applied'
            AND applied_object_type IS NULL
            AND applied_object_id IS NULL
            AND applied_object_revision IS NULL
            AND applied_at IS NULL
        )
    )
) STRICT;

CREATE INDEX ix_drafts_status_updated
    ON drafts(status, updated_at DESC, draft_id);

CREATE INDEX ix_drafts_target
    ON drafts(target_object_type, target_id, status, updated_at DESC)
    WHERE target_id IS NOT NULL;

CREATE INDEX ix_drafts_terminal
    ON drafts(terminal_at, draft_id)
    WHERE terminal_at IS NOT NULL;

CREATE TRIGGER trg_drafts_revision
BEFORE UPDATE ON drafts
WHEN NEW.draft_revision <> OLD.draft_revision + 1
BEGIN
    SELECT RAISE(ABORT, 'drafts.draft_revision must increment by one');
END;

CREATE TRIGGER trg_drafts_terminal_immutable
BEFORE UPDATE ON drafts
WHEN OLD.status IN ('applied', 'rejected', 'discarded', 'expired')
BEGIN
    SELECT RAISE(ABORT, 'terminal drafts are immutable');
END;

CREATE TRIGGER trg_drafts_identity_immutable
BEFORE UPDATE OF
    draft_id,
    target_object_type,
    operation_type,
    target_id,
    source_actor,
    created_at
ON drafts
WHEN
    NEW.draft_id <> OLD.draft_id
    OR NEW.target_object_type <> OLD.target_object_type
    OR NEW.operation_type <> OLD.operation_type
    OR NEW.target_id IS NOT OLD.target_id
    OR NEW.source_actor <> OLD.source_actor
    OR NEW.created_at <> OLD.created_at
BEGIN
    SELECT RAISE(ABORT, 'draft identity fields are immutable');
END;

CREATE TABLE idempotency_records (
    idempotency_key TEXT PRIMARY KEY
        CHECK (length(idempotency_key) = 36),
    actor_kind TEXT NOT NULL
        CHECK (actor_kind IN ('user_ui', 'mcp')),
    operation TEXT NOT NULL
        CHECK (operation IN (
            'knowledge_model.change',
            'draft.create',
            'draft.update',
            'draft.rebase',
            'draft.submit_review',
            'draft.apply',
            'draft.reject',
            'draft.discard'
        )),
    request_hash TEXT NOT NULL
        CHECK (
            length(request_hash) = 64
            AND request_hash NOT GLOB '*[^0-9a-f]*'
        ),
    result_json TEXT NOT NULL
        CHECK (
            json_valid(result_json)
            AND json_type(result_json) = 'object'
            AND length(CAST(result_json AS BLOB)) <= 2097152
        ),
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    CHECK (expires_at >= created_at)
) STRICT;

CREATE INDEX ix_idempotency_expiry
    ON idempotency_records(expires_at, idempotency_key);

CREATE TRIGGER trg_idempotency_no_update
BEFORE UPDATE ON idempotency_records
BEGIN
    SELECT RAISE(ABORT, 'idempotency_records is immutable');
END;

CREATE TABLE audit_events (
    event_id TEXT PRIMARY KEY
        CHECK (length(event_id) = 36),
    occurred_at TEXT NOT NULL,
    actor_kind TEXT NOT NULL
        CHECK (actor_kind IN ('user_ui', 'mcp', 'system')),
    action TEXT NOT NULL
        CHECK (action IN (
            'vault.initialize',
            'knowledge_model.change',
            'source.create',
            'source.update',
            'source.archive',
            'source.restore',
            'source_revision.create',
            'knowledge.create',
            'knowledge.update',
            'knowledge.archive',
            'knowledge.restore',
            'source_reference.create',
            'relation.create',
            'relation.update',
            'relation.retract',
            'relation.restore',
            'draft.create',
            'draft.update',
            'draft.rebase',
            'draft.submit_review',
            'draft.apply',
            'draft.reject',
            'draft.discard',
            'draft.expire',
            'draft.cleanup',
            'backup.create',
            'backup.download',
            'backup.restore',
            'mcp_token.rotate'
        )),
    target_object_type TEXT
        CHECK (
            target_object_type IS NULL
            OR target_object_type IN (
                'vault',
                'knowledge_model',
                'knowledge_type',
                'template',
                'relation_type',
                'source_rule',
                'workflow',
                'source',
                'source_revision',
                'knowledge',
                'source_reference',
                'relation',
                'draft',
                'backup',
                'mcp_token'
            )
        ),
    target_id TEXT
        CHECK (target_id IS NULL OR length(target_id) = 36),
    target_revision INTEGER
        CHECK (target_revision IS NULL OR target_revision >= 1),
    correlation_id TEXT
        CHECK (correlation_id IS NULL OR length(correlation_id) <= 128),
    result TEXT NOT NULL
        CHECK (result IN ('success', 'failed', 'cancelled', 'started')),
    details_json TEXT NOT NULL DEFAULT '{}'
        CHECK (
            json_valid(details_json)
            AND json_type(details_json) = 'object'
            AND length(CAST(details_json AS BLOB)) <= 4096
        )
) STRICT;

CREATE INDEX ix_audit_occurred
    ON audit_events(occurred_at DESC, event_id);

CREATE INDEX ix_audit_target
    ON audit_events(target_object_type, target_id, occurred_at DESC);

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

CREATE VIRTUAL TABLE knowledge_fts USING fts5(
    knowledge_id UNINDEXED,
    title,
    summary,
    type_key,
    tags,
    content,
    tokenize = 'trigram'
);

CREATE TRIGGER trg_sources_no_delete
BEFORE DELETE ON sources
BEGIN
    SELECT RAISE(ABORT, 'sources must be archived, not deleted');
END;

CREATE TRIGGER trg_source_revisions_no_update
BEFORE UPDATE ON source_revisions
BEGIN
    SELECT RAISE(ABORT, 'source_revisions is append-only');
END;

CREATE TRIGGER trg_source_revisions_no_delete
BEFORE DELETE ON source_revisions
BEGIN
    SELECT RAISE(ABORT, 'source_revisions is append-only');
END;

CREATE TRIGGER trg_source_locators_no_update
BEFORE UPDATE ON source_locators
BEGIN
    SELECT RAISE(ABORT, 'source_locators is append-only');
END;

CREATE TRIGGER trg_source_locators_no_delete
BEFORE DELETE ON source_locators
BEGIN
    SELECT RAISE(ABORT, 'source_locators is append-only');
END;

CREATE TRIGGER trg_knowledge_items_no_delete
BEFORE DELETE ON knowledge_items
BEGIN
    SELECT RAISE(ABORT, 'knowledge_items must be archived, not deleted');
END;

CREATE TRIGGER trg_knowledge_revisions_no_update
BEFORE UPDATE ON knowledge_revisions
BEGIN
    SELECT RAISE(ABORT, 'knowledge_revisions is append-only');
END;

CREATE TRIGGER trg_knowledge_revisions_no_delete
BEFORE DELETE ON knowledge_revisions
BEGIN
    SELECT RAISE(ABORT, 'knowledge_revisions is append-only');
END;

CREATE TRIGGER trg_source_references_no_update
BEFORE UPDATE ON source_references
BEGIN
    SELECT RAISE(ABORT, 'source_references is append-only');
END;

CREATE TRIGGER trg_source_references_no_delete
BEFORE DELETE ON source_references
BEGIN
    SELECT RAISE(ABORT, 'source_references is append-only');
END;

CREATE TRIGGER trg_knowledge_relations_no_delete
BEFORE DELETE ON knowledge_relations
BEGIN
    SELECT RAISE(ABORT, 'knowledge_relations must be retracted, not deleted');
END;

CREATE TRIGGER trg_knowledge_relation_revisions_no_update
BEFORE UPDATE ON knowledge_relation_revisions
BEGIN
    SELECT RAISE(ABORT, 'knowledge_relation_revisions is append-only');
END;

CREATE TRIGGER trg_knowledge_relation_revisions_no_delete
BEFORE DELETE ON knowledge_relation_revisions
BEGIN
    SELECT RAISE(ABORT, 'knowledge_relation_revisions is append-only');
END;

INSERT INTO knowledge_types(
    type_id,
    type_key,
    name,
    description,
    is_enabled,
    sort_order,
    created_at,
    updated_at
)
VALUES
    (
        '00000000-0000-4000-8000-000000001001',
        'note',
        '笔记',
        '个人记录和零散想法',
        1,
        10,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000001002',
        'article',
        '文章',
        '文章、论文或网页内容的整理结果',
        1,
        20,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000001003',
        'book',
        '书籍',
        '书籍阅读和摘录',
        1,
        30,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000001004',
        'podcast',
        '播客',
        '播客、音频和访谈内容',
        1,
        40,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000001005',
        'plan',
        '计划',
        '目标、计划和个人进度',
        1,
        50,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000001006',
        'reflection',
        '反思',
        '复盘、经验和学习总结',
        1,
        60,
        $createdAtUtc,
        $createdAtUtc
    );

INSERT INTO relation_types(
    relation_type_id,
    relation_key,
    name,
    description,
    is_directed,
    is_enabled,
    sort_order,
    created_at,
    updated_at
)
VALUES
    (
        '00000000-0000-4000-8000-000000002001',
        'supports',
        '支持',
        '一个知识为另一个知识提供支持',
        1,
        1,
        10,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000002002',
        'refutes',
        '反驳',
        '一个知识与另一个知识相冲突',
        1,
        1,
        20,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000002003',
        'complements',
        '补充',
        '一个知识补充另一个知识',
        1,
        1,
        30,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000002004',
        'exemplifies',
        '举例',
        '一个知识是另一个知识的实例',
        1,
        1,
        40,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000002005',
        'applies',
        '应用',
        '一个知识应用于另一个知识',
        1,
        1,
        50,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000002006',
        'depends_on',
        '依赖',
        '一个知识依赖另一个知识',
        1,
        1,
        60,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000002007',
        'belongs_to',
        '属于',
        '一个知识属于另一个知识',
        1,
        1,
        70,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000002008',
        'similar_to',
        '相似',
        '两个知识存在主题或内容相似性',
        0,
        1,
        80,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000002009',
        'derived_from',
        '来源于',
        '一个知识来源于另一个知识',
        1,
        1,
        90,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000002010',
        'validated_by_practice',
        '实践验证',
        '一个知识在实践中得到验证',
        1,
        1,
        100,
        $createdAtUtc,
        $createdAtUtc
    );

INSERT INTO source_rules(
    source_rule_id,
    source_kind,
    metadata_schema_json,
    supported_locator_types_json,
    is_enabled,
    created_at,
    updated_at
)
VALUES
    (
        '00000000-0000-4000-8000-000000003001',
        'web_page',
        '{"required":["url"],"properties":{"url":{"type":"string"},"author":{"type":"string"},"publishedAt":{"type":"date"}},"additionalProperties":false}',
        '["section","paragraph"]',
        1,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000003002',
        'local_file',
        '{"required":["originalFileName"],"properties":{"originalFileName":{"type":"string"},"mediaType":{"type":"string"},"fileSize":{"type":"number"}},"additionalProperties":false}',
        '["section","paragraph"]',
        1,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000003003',
        'book',
        '{"required":[],"properties":{"authors":{"type":"array","itemType":"string"},"isbn":{"type":"string"},"publisher":{"type":"string"},"publishedAt":{"type":"date"}},"additionalProperties":false}',
        '["section","paragraph"]',
        1,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000003004',
        'podcast',
        '{"required":[],"properties":{"showName":{"type":"string"},"episodeTitle":{"type":"string"},"publishedAt":{"type":"date"},"durationMs":{"type":"number"}},"additionalProperties":false}',
        '["section","paragraph"]',
        1,
        $createdAtUtc,
        $createdAtUtc
    ),
    (
        '00000000-0000-4000-8000-000000003005',
        'other',
        '{"required":[],"properties":{"note":{"type":"string"}},"additionalProperties":false}',
        '["section","paragraph"]',
        1,
        $createdAtUtc,
        $createdAtUtc
    );
