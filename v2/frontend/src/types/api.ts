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
  rawTarget: string;
  displayText: string;
  targetHeading?: string | null;
  resolutionStatus: "resolved" | "unresolved" | "ambiguous";
  target?: LinkTargetView | null;
  candidates: LinkTargetView[];
  startOffset: number;
  endOffset: number;
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

export interface ImportPreviewItem {
  relativePath: string;
  virtualPath: string;
  format: VaultFormat;
  sizeBytes: number;
  sha256: string;
  state: ImportPreviewItemState;
  reason?: string | null;
}

export interface ImportPreviewResult {
  token: string;
  expiresAt: UtcTimestamp;
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
  updateBacklinks: boolean;
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
  pageSize?: number;
  cursor?: string;
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
