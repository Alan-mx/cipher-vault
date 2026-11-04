import type {
  ActorKind,
  CursorPage,
  GuidString,
  LockReason,
  UtcTimestamp,
  VaultFormat,
  VaultState,
} from "./domain";

export interface VaultStatus {
  state: VaultState;
  isInitialized: boolean;
  isUnlocked: boolean;
  startedAtUtc?: UtcTimestamp | null;
  lastActivityAtUtc?: UtcTimestamp | null;
  idleExpiresAtUtc?: UtcTimestamp | null;
  idleTimeoutMinutes: number;
  idleRemainingSeconds: number | null;
  lockReason: LockReason;
}

export interface AntiforgeryTokenResponse {
  token: string;
}

export interface CatalogItem {
  documentId: GuidString;
  title: string;
  virtualPath: string;
  format: VaultFormat;
  revision: number;
  tags: string[];
  updatedAt: UtcTimestamp;
}

export type CatalogPage = CursorPage<CatalogItem>;

export interface TagItem {
  name: string;
  documentCount: number;
}

export type TagPage = CursorPage<TagItem>;

export interface DocumentMetadata {
  documentId: GuidString;
  title: string;
  virtualPath: string;
  format: VaultFormat;
  revision: number;
  tags: string[];
  createdAt: UtcTimestamp;
  updatedAt: UtcTimestamp;
}

export interface DocumentContentChunk {
  documentId: GuidString;
  revision: number;
  content: string;
  offset: number;
  nextOffset: number;
  totalLength: number;
  done: boolean;
}

export interface DocumentWriteResult {
  documentId: GuidString;
  revision: number;
  virtualPath: string;
  committedAt: UtcTimestamp;
  auditEventId?: GuidString | null;
}

export interface DocumentHeading {
  level: number;
  text: string;
  path: string[];
}

export interface FrontMatterFieldView {
  key: string;
  valueType: string;
  valueJson: string;
}

export interface LinkTargetView {
  documentId: GuidString;
  title: string;
  virtualPath: string;
}

export interface OutgoingLinkView {
  linkKind: "wiki" | "markdown";
  rawTarget: string;
  displayText: string;
  targetHeading?: string | null;
  resolutionStatus: "resolved" | "unresolved" | "ambiguous";
  target?: LinkTargetView | null;
  candidates: LinkTargetView[];
  startOffset: number;
  endOffset: number;
  destinationStartOffset?: number | null;
  destinationEndOffset?: number | null;
}

export interface DocumentMap {
  metadata: DocumentMetadata;
  headings: DocumentHeading[];
  frontMatterFields: FrontMatterFieldView[];
  outgoingLinks: OutgoingLinkView[];
  backlinkCount: number;
  headingsTruncated: boolean;
  frontMatterTruncated: boolean;
  linksTruncated: boolean;
}

export interface BacklinkItem {
  sourceDocumentId: GuidString;
  sourceTitle: string;
  sourceVirtualPath: string;
  sourceRevision: number;
  occurrenceCount: number;
  firstTargetHeading?: string | null;
  updatedAt: UtcTimestamp;
}

export type BacklinkPage = CursorPage<BacklinkItem>;

export type RelatedReasonCode =
  | "same_project"
  | "same_type"
  | "shared_tags"
  | "shared_properties"
  | "two_hop_link"
  | "similar_title";

export interface RelatedReason {
  code: RelatedReasonCode;
  label: string;
}

export interface RelatedDocumentItem {
  documentId: GuidString;
  title: string;
  virtualPath: string;
  revision: number;
  tags: string[];
  score: number;
  reasons: RelatedReason[];
  updatedAt: UtcTimestamp;
}

export interface RelatedDocumentsResponse {
  items: RelatedDocumentItem[];
}

export type KnowledgeViewField = "project" | "type" | "maturity";

export interface KnowledgeFacetItem {
  key: string | null;
  label: string;
  documentCount: number;
  isUnset: boolean;
}

export interface KnowledgeFacetPage {
  field: KnowledgeViewField;
  items: KnowledgeFacetItem[];
  truncated: boolean;
}

export interface KnowledgeViewItem {
  documentId: GuidString;
  title: string;
  virtualPath: string;
  format: VaultFormat;
  revision: number;
  tags: string[];
  updatedAt: UtcTimestamp;
}

export interface KnowledgeViewPage {
  items: KnowledgeViewItem[];
  nextCursor: string | null;
}

export interface KnowledgeViewDocumentQuery {
  facetKey?: string | undefined;
  unset?: boolean | undefined;
  activeOnly?: boolean | undefined;
  pageSize?: number | undefined;
  cursor?: string | undefined;
}

export interface UnlinkedMentionOccurrence {
  startOffset: number;
  endOffset: number;
  context: string;
}

export interface UnlinkedMentionItem {
  sourceDocumentId: GuidString;
  sourceTitle: string;
  sourceVirtualPath: string;
  sourceRevision: number;
  occurrenceCount: number;
  occurrences: UnlinkedMentionOccurrence[];
}

export interface UnlinkedMentionPage {
  targetDocumentId: GuidString;
  items: UnlinkedMentionItem[];
  nextCursor?: string | null;
}

export interface SearchCoverage {
  totalActiveDocuments: number;
  scannedDocuments: number;
  matchedDocuments: number;
  returnedDocuments: number;
  truncated: boolean;
}

export interface SearchHit {
  documentId: GuidString;
  title: string;
  virtualPath: string;
  revision: number;
  snippet: string;
  score: number;
}

export interface SearchPage {
  hits: SearchHit[];
  coverage: SearchCoverage;
  nextCursor?: string | null;
}

export interface GraphNode {
  documentId: GuidString;
  title: string;
  virtualPath: string;
  revision: number;
  tags: string[];
  depth: number;
  degree: number;
  isCenter: boolean;
}

export interface GraphEdge {
  sourceDocumentId: GuidString;
  targetDocumentId: GuidString;
  linkCount: number;
}

export interface LocalGraphResult {
  nodes: GraphNode[];
  edges: GraphEdge[];
  truncated: boolean;
}

export interface RevisionItem {
  revisionId: GuidString;
  documentId: GuidString;
  revision: number;
  title: string;
  virtualPath: string;
  createdAt: UtcTimestamp;
  retainUntil: UtcTimestamp;
}

export interface RevisionContent extends RevisionItem {
  format: VaultFormat;
  tags: string[];
  content: string;
}

export type RevisionPage = CursorPage<RevisionItem>;

export interface TrashItem {
  documentId: GuidString;
  title: string;
  virtualPath: string;
  revision: number;
  deletedAt: UtcTimestamp;
  purgeEligibleAt: UtcTimestamp;
}

export type TrashPage = CursorPage<TrashItem>;

export interface TrashDeleteResult {
  documentId: GuidString;
  revision: number;
  deletedAt: UtcTimestamp;
  purgeEligibleAt: UtcTimestamp;
  auditEventId?: GuidString | null;
}

export interface ImportPreviewRequest {
  sourceRoot: string;
}

export type ImportPreviewItemState = "ready" | "skipped_conflict" | "rejected";
export type ImportIdentitySource = "frontmatter" | "manifest" | "generated";

export interface ImportPreviewItem {
  relativePath: string;
  virtualPath: string;
  title: string;
  format: VaultFormat;
  sizeBytes: number;
  sha256: string;
  documentId?: GuidString | null;
  identitySource?: ImportIdentitySource | null;
  contentHash?: string | null;
  state: ImportPreviewItemState;
  reason?: string | null;
}

export interface ImportPreviewResult {
  token: string;
  expiresAt: UtcTimestamp;
  manifestVersion?: number | null;
  manifestSha256?: string | null;
  readyCount: number;
  skippedCount: number;
  rejectedCount: number;
  items: ImportPreviewItem[];
}

export interface ImportCommitItem {
  relativePath: string;
  documentId?: GuidString | null;
  revision?: number | null;
  result: string;
  errorCode?: string | null;
}

export interface ImportCommitResult {
  imported: number;
  skipped: number;
  failed: number;
  items: ImportCommitItem[];
  auditEventId?: GuidString | null;
}

export interface AuditItem {
  eventId: GuidString;
  occurredAt: UtcTimestamp;
  actor: ActorKind;
  action: string;
  documentId?: GuidString | null;
  revision?: number | null;
  result: string;
  detailsJson: string;
}

export type AuditPage = CursorPage<AuditItem>;

export interface McpTokenStatus {
  configured: boolean;
  version?: number | null;
  createdAtUtc?: UtcTimestamp | null;
  rotatedAtUtc?: UtcTimestamp | null;
}

export interface McpTokenRotateResponse {
  token: string;
  version: number;
  createdAtUtc: UtcTimestamp;
  rotatedAtUtc: UtcTimestamp;
}

export interface PurgeResult {
  auditEventId?: GuidString | null;
}

export interface CatalogQuery {
  folder?: string;
  format?: VaultFormat;
  tags?: string[];
  orphanOnly?: boolean;
  pageSize?: number;
  cursor?: string;
}

export interface CreateDocumentRequest {
  title: string;
  virtualPath?: string;
  folder?: string;
  format: VaultFormat;
  content: string;
  tags?: string[];
}

export interface UpdateDocumentRequest {
  title: string;
  virtualPath?: string;
  content: string;
  tags?: string[];
}

export interface CopyDocumentRequest {
  title?: string;
  virtualPath?: string;
}

export interface TrashRestoreRequest {
  replacementPath?: string;
}

export interface SearchQuery {
  query: string;
  tags?: string[];
  format?: VaultFormat;
  updatedAfter?: UtcTimestamp;
  updatedBefore?: UtcTimestamp;
  hasBacklinks?: boolean;
  orphanOnly?: boolean;
  propertyMatch?: SearchPropertyMatch;
  propertyFilters?: SearchPropertyFilter[];
  pageSize?: number;
  cursor?: string;
}

export type SearchPropertyOperator =
  "exists" | "eq" | "contains" | "in" | "gte" | "lte";

export type SearchPropertyMatch = "all" | "any";

export interface SearchPropertyFilter {
  field: string;
  operator: SearchPropertyOperator;
  value: unknown;
}

export interface LocalGraphQuery {
  centerDocumentId: GuidString;
  depth?: 1 | 2 | 3;
  tags?: string[];
  folder?: string;
  maxNodes?: number;
  maxEdges?: number;
}

export interface AuditQuery {
  actor?: ActorKind;
  action?: string;
  documentId?: GuidString;
  occurredAfter?: UtcTimestamp;
  occurredBefore?: UtcTimestamp;
  pageSize?: number;
  cursor?: string;
}

export type BackupFileStatus = "manifest_readable" | "invalid" | "verified";

export interface BackupListItem {
  fileName: string;
  bytes: number;
  modifiedAtUtc: UtcTimestamp;
  createdAtUtc?: UtcTimestamp | null;
  formatVersion?: number | null;
  status: BackupFileStatus;
  errorCode?: string | null;
}

export interface BackupListResponse {
  items: BackupListItem[];
  nextCursor?: string | null;
}

export interface BackupCreateResponse {
  backup: BackupListItem;
}

export type GitBackupState =
  | "disabled"
  | "idle"
  | "pending"
  | "running"
  | "retry_wait"
  | "rotation_required"
  | "failed";

export interface GitBackupStatus {
  state: GitBackupState;
  pending: boolean;
  pushPending: boolean;
  nextAttemptAtUtc?: UtcTimestamp | null;
  lastSuccessAtUtc?: UtcTimestamp | null;
  lastCommitShortSha?: string | null;
  lastPushAtUtc?: UtcTimestamp | null;
  lastErrorCode?: string | null;
  rotationRequired: boolean;
  repositoryObjectBytes?: number | null;
}

export interface BackupQuery {
  pageSize?: number;
  cursor?: string;
}

export interface VersionInfo {
  productVersion: string;
  commit: string;
  rid: string;
  runtimeVersion: string;
  aspNetCoreVersion: string;
}

export type McpWriteMode = "draft" | "direct";

export interface McpWriteModeStatus {
  mode: McpWriteMode;
  sessionId?: GuidString | null;
  sessionGeneration?: number | null;
  updatedAt?: UtcTimestamp | null;
  effective: boolean;
}

export type DocumentDraftType = "create" | "update" | "trash";

export type DocumentDraftQueue = "pending_review" | "working" | "history";

export type DocumentDraftStatus =
  | "draft"
  | "pending_review"
  | "conflict"
  | "applied"
  | "rejected"
  | "discarded"
  | "expired";

export interface DocumentDraftQueueSummary {
  pendingReview: number;
  working: number;
  history: number;
}

export interface DocumentDraftItem {
  draftId: GuidString;
  draftRevision: number;
  draftType: DocumentDraftType;
  status: DocumentDraftStatus;
  targetDocumentId?: GuidString | null;
  baseDocumentRevision?: number | null;
  futureDocumentId?: GuidString | null;
  sourceActor: ActorKind;
  title?: string | null;
  virtualPath?: string | null;
  format?: VaultFormat | null;
  createdAt: UtcTimestamp;
  updatedAt: UtcTimestamp;
  terminalAt?: UtcTimestamp | null;
  appliedDocumentId?: GuidString | null;
  appliedDocumentRevision?: number | null;
}

export type DocumentDraftPage = CursorPage<DocumentDraftItem>;

export interface DocumentDraftReviewSource {
  documentId?: GuidString | null;
  title?: string | null;
  virtualPath?: string | null;
  reference?: string | null;
  excerpt: string;
}

export interface DocumentDraftReviewContext {
  summary?: string | null;
  sources: DocumentDraftReviewSource[];
}

export interface DocumentDraftFieldChange {
  fieldKind:
    | "title"
    | "virtual_path"
    | "format"
    | "tags"
    | "frontmatter"
    | "body"
    | "trash";
  key?: string | null;
  beforeExists: boolean;
  beforeValueType?: string | null;
  before?: string | null;
  beforeTruncated: boolean;
  afterExists: boolean;
  afterValueType?: string | null;
  after?: string | null;
  afterTruncated: boolean;
}

export interface DocumentDraftStructuredChanges {
  changes: DocumentDraftFieldChange[];
  totalChangeCount: number;
  truncated: boolean;
}

export interface DocumentDraftOperationCount {
  type:
    | "replace_body"
    | "append_body"
    | "set_property"
    | "remove_property"
    | "add_tag"
    | "remove_tag";
  count: number;
}

export interface DocumentDraftOperationSummary {
  total: number;
  items: DocumentDraftOperationCount[];
}

export interface DocumentDraftBodyEditCapability {
  canSave: boolean;
  reason?: string | null;
}

export interface DocumentDraftPreview {
  content: string;
  tags: string[];
  unifiedDiff: string;
  diffTruncated: boolean;
  operationCount: number;
  structuredChanges: DocumentDraftStructuredChanges;
  operationSummary: DocumentDraftOperationSummary;
  bodyEdit: DocumentDraftBodyEditCapability;
}

export interface DocumentDraftDetail {
  metadata: DocumentDraftItem;
  payload: Record<string, unknown>;
  reviewContext?: DocumentDraftReviewContext | null;
  preview: DocumentDraftPreview;
}

export interface DocumentDraftMutationResult {
  draftId: GuidString;
  draftRevision: number;
  status: DocumentDraftStatus;
  futureDocumentId?: GuidString | null;
  updatedAt: UtcTimestamp;
}

export interface DocumentDraftApplyResult {
  draftId: GuidString;
  draftRevision: number;
  status: DocumentDraftStatus;
  documentWriteResult: DocumentWriteResult;
}

export interface CreateDocumentDraftRequest {
  draftType: DocumentDraftType;
  targetDocumentId?: GuidString | null;
  baseDocumentRevision?: number | null;
  payload: Record<string, unknown>;
}

export interface UpdateDocumentDraftRequest {
  expectedDraftRevision: number;
  payload: Record<string, unknown>;
}

export interface DocumentDraftQuery {
  queue?: DocumentDraftQueue | undefined;
  status?: DocumentDraftStatus | undefined;
  type?: DocumentDraftType | undefined;
  sourceActor?: "user_ui" | "mcp" | undefined;
  targetDocumentId?: GuidString | undefined;
  updatedAfter?: UtcTimestamp | undefined;
  updatedBefore?: UtcTimestamp | undefined;
  pageSize?: number | undefined;
  cursor?: string | undefined;
}

export type DraftBatchAction = "submit_review" | "reject" | "discard";

export type DraftBatchResultKind =
  "success" | "revision_conflict" | "not_editable" | "failed";

export interface DraftBatchItemResult {
  draftId: GuidString;
  action: DraftBatchAction;
  kind: DraftBatchResultKind;
  errorCode?: string;
  message?: string;
}
