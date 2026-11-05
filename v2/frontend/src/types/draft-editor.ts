import type { SourceReference } from "./knowledge-base";

export interface KnowledgeDraftPayload {
  title: string;
  summary: string;
  knowledgeTypeId: string;
  templateId: string | null;
  contentMarkdown: string;
  properties: Record<string, unknown>;
  tags: string[];
  sourceReferences: SourceReference[];
}

export interface SourceDraftPayload {
  sourceKind: string;
  title: string;
  metadata: Record<string, unknown>;
  contentFormat: string;
  contentText: string;
}

export interface RelationDraftPayload {
  relationTypeId: string;
  sourceKnowledgeId: string;
  targetKnowledgeId: string;
  sourceKnowledgeRevision: number;
  targetKnowledgeRevision: number;
  rationale: string;
  evidence: unknown[];
}

export type EditableDraftPayload =
  KnowledgeDraftPayload | SourceDraftPayload | RelationDraftPayload;

export type EditableDraftObjectType = "knowledge" | "source" | "relation";
