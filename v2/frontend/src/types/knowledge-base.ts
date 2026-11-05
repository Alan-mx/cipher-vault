export interface PagedResult<T> {
  items: T[];
  nextCursor: string | null;
  hasMore: boolean;
  totalCount: number | null;
}

export interface KnowledgeType {
  typeId: string | null;
  key: string;
  name: string;
  description: string | null;
  isEnabled: boolean;
  sortOrder: number;
  properties: KnowledgeProperty[];
}

export interface KnowledgeProperty {
  propertyId: string | null;
  key: string;
  name: string;
  valueType: string;
  required: boolean;
  defaultValue: unknown;
  constraints: Record<string, unknown>;
  isEnabled: boolean;
  sortOrder: number;
}

export interface KnowledgeTemplate {
  templateId: string | null;
  knowledgeTypeId: string;
  key: string;
  name: string;
  description: string | null;
  bodyTemplate: string;
  fields: unknown[];
  isEnabled: boolean;
  sortOrder: number;
}

export interface RelationType {
  relationTypeId: string | null;
  key: string;
  name: string;
  description: string | null;
  isDirected: boolean;
  isEnabled: boolean;
  sortOrder: number;
}

export interface SourceRule {
  sourceRuleId: string | null;
  kind: string;
  metadataSchema: Record<string, unknown>;
  supportedLocatorTypes: string[];
  isEnabled: boolean;
}

export interface Workflow {
  workflowId: string | null;
  key: string;
  name: string;
  description: string | null;
  targetObjectTypes: string[];
  constraints: Record<string, unknown>;
  isEnabled: boolean;
  sortOrder: number;
}

export interface KnowledgeModel {
  version: number;
  updatedAt: string;
  knowledgeTypes: KnowledgeType[];
  templates: KnowledgeTemplate[];
  relationTypes: RelationType[];
  sourceRules: SourceRule[];
  workflows: Workflow[];
}

export interface SourceSummary {
  sourceId: string;
  kind: string;
  title: string;
  status: string;
  currentRevision: number;
  updatedAt: string;
}

export interface SourceDetail extends SourceSummary {
  metadata: Record<string, unknown>;
  currentRevisionId: string;
  createdAt: string;
  archivedAt: string | null;
}

export interface SourceRevision {
  sourceId: string;
  sourceRevisionId: string;
  revision: number;
  kind: string;
  title: string;
  metadata: Record<string, unknown>;
  contentFormat: string;
  contentLength: number;
  createdAt: string;
}

export interface SourceContentChunk {
  sourceId: string;
  sourceRevision: number;
  contentFormat: string;
  offset: number;
  nextOffset: number;
  totalLength: number;
  text: string;
  done: boolean;
}

export interface SourceLocator {
  locatorId: string;
  type: string;
  value: unknown;
  label: string | null;
  startOffset: number;
  endOffset: number;
  ordinal: number;
  parentLocatorId: string | null;
}

export interface SourceLocatorContent {
  sourceId: string;
  sourceRevision: number;
  locator: SourceLocator;
  offset: number;
  nextOffset: number;
  length: number;
  text: string;
  done: boolean;
}

export interface KnowledgeSummary {
  knowledgeId: string;
  title: string;
  summary: string;
  knowledgeTypeId: string;
  knowledgeTypeKey: string;
  tags: string[];
  status: string;
  revision: number;
  updatedAt: string;
}

export interface SourceReference {
  sourceReferenceId: string;
  sourceId: string;
  sourceRevision: number;
  locatorType: string;
  locatorValue: unknown;
  excerpt: string;
  extractionMetadata: Record<string, unknown>;
}

export interface KnowledgeDetail extends KnowledgeSummary {
  templateId: string | null;
  currentRevision: number;
  currentRevisionId: string;
  properties: Record<string, unknown>;
  sourceReferences: SourceReference[];
  createdAt: string;
}

export interface KnowledgeContentChunk {
  knowledgeId: string;
  knowledgeRevision: number;
  offset: number;
  nextOffset: number;
  totalLength: number;
  markdown: string;
  done: boolean;
}

export interface DraftRecord {
  draftId: string;
  draftRevision: number;
  targetObjectType: string;
  operationType: string;
  targetId: string | null;
  baseRevision: number | null;
  baseModelVersion: number;
  payloadVersion: number;
  payload: Record<string, unknown>;
  baseSnapshot: Record<string, unknown> | null;
  status: string;
  sourceActor: string;
  createdAt: string;
  updatedAt: string;
  terminalAt: string | null;
  appliedObjectType: string | null;
  appliedObjectId: string | null;
  appliedObjectRevision: number | null;
  appliedAt: string | null;
}

export interface DraftListItem {
  draftId: string;
  draftRevision: number;
  targetObjectType: string;
  operationType: string;
  targetId: string | null;
  baseRevision: number | null;
  status: string;
  sourceActor: string;
  title: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface DraftDetail {
  record: DraftRecord;
  preview: {
    summary: string;
    diff: string;
    diffTruncated: boolean;
    sourceReferences: SourceReference[];
    validationIssues: string[];
  };
}

export interface DashboardView {
  recentKnowledge: KnowledgeSummary[];
  recentSources: SourceSummary[];
  pendingDrafts: DraftListItem[];
  inbox: DraftListItem[];
  recentRelationChanges: unknown[];
  counts: Record<string, number>;
}

export interface CreateKnowledgeRetrospectiveDraftRequest {
  topic: string;
  knowledgeIds: string[];
}

export interface DraftMutation {
  draftId: string;
  draftRevision: number;
  status: string;
  targetObjectType: string;
  operationType: string;
  updatedAt: string;
}

export interface CreateKnowledgeSynthesisDraftRequest {
  topic: string;
  question?: string | null;
  knowledgeIds: string[];
  knowledgeTypeId: string;
}

export interface KnowledgeSynthesisHandoff {
  draftId: string;
  topic: string;
  question: string;
  knowledgeTypeId: string;
  knowledgeTypeKey: string;
  knowledgeTypeName: string;
  knowledge: Array<{ knowledgeId: string; title: string }>;
}
