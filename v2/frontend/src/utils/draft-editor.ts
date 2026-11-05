import type {
  EditableDraftObjectType,
  EditableDraftPayload,
  KnowledgeDraftPayload,
  RelationDraftPayload,
  SourceDraftPayload,
} from "../types/draft-editor";

export const parseDraftPayload = (
  targetType: EditableDraftObjectType,
  payload: Record<string, unknown>,
): EditableDraftPayload => {
  switch (targetType) {
    case "knowledge":
      return {
        title: stringValue(payload.title),
        summary: stringValue(payload.summary),
        knowledgeTypeId: stringValue(payload.knowledgeTypeId),
        templateId:
          typeof payload.templateId === "string" ? payload.templateId : null,
        contentMarkdown: stringValue(payload.contentMarkdown),
        properties: objectValue(payload.properties),
        tags: stringArrayValue(payload.tags),
        sourceReferences: Array.isArray(payload.sourceReferences)
          ? (payload.sourceReferences as KnowledgeDraftPayload["sourceReferences"])
          : [],
      };
    case "source":
      return {
        sourceKind: stringValue(payload.sourceKind),
        title: stringValue(payload.title),
        metadata: objectValue(payload.metadata),
        contentFormat: stringValue(payload.contentFormat),
        contentText: stringValue(payload.contentText),
      };
    case "relation":
      return {
        relationTypeId: stringValue(payload.relationTypeId),
        sourceKnowledgeId: stringValue(payload.sourceKnowledgeId),
        targetKnowledgeId: stringValue(payload.targetKnowledgeId),
        sourceKnowledgeRevision: numberValue(payload.sourceKnowledgeRevision),
        targetKnowledgeRevision: numberValue(payload.targetKnowledgeRevision),
        rationale: stringValue(payload.rationale),
        evidence: Array.isArray(payload.evidence) ? payload.evidence : [],
      };
  }
};

export const serializeDraftPayload = (
  targetType: EditableDraftObjectType,
  form: EditableDraftPayload,
): Record<string, unknown> => {
  switch (targetType) {
    case "knowledge": {
      const knowledge = form as KnowledgeDraftPayload;
      return {
        title: knowledge.title,
        summary: knowledge.summary,
        knowledgeTypeId: knowledge.knowledgeTypeId,
        templateId: knowledge.templateId,
        contentMarkdown: knowledge.contentMarkdown,
        properties: knowledge.properties,
        tags: knowledge.tags,
        sourceReferences: knowledge.sourceReferences,
      };
    }
    case "source": {
      const source = form as SourceDraftPayload;
      return {
        sourceKind: source.sourceKind,
        title: source.title,
        metadata: source.metadata,
        contentFormat: source.contentFormat,
        contentText: source.contentText,
      };
    }
    case "relation": {
      const relation = form as RelationDraftPayload;
      return {
        relationTypeId: relation.relationTypeId,
        sourceKnowledgeId: relation.sourceKnowledgeId,
        targetKnowledgeId: relation.targetKnowledgeId,
        sourceKnowledgeRevision: relation.sourceKnowledgeRevision,
        targetKnowledgeRevision: relation.targetKnowledgeRevision,
        rationale: relation.rationale,
        evidence: relation.evidence,
      };
    }
  }
};

export const canonicalizeDraftPayload = (
  targetType: EditableDraftObjectType,
  form: EditableDraftPayload,
): string => canonicalize(serializeDraftPayload(targetType, form));

export const isDraftPayloadDirty = (
  targetType: EditableDraftObjectType,
  baseline: EditableDraftPayload | null,
  form: EditableDraftPayload | null,
): boolean => {
  if (!baseline || !form) {
    return false;
  }
  return (
    canonicalizeDraftPayload(targetType, baseline) !==
    canonicalizeDraftPayload(targetType, form)
  );
};

export const draftFieldId = (fieldPath: string): string =>
  `draft-field-${fieldPath.replace(/[^a-zA-Z0-9_-]+/g, "-")}`;

export const cloneDraftPayload = <T extends EditableDraftPayload>(
  value: T,
): T => JSON.parse(JSON.stringify(value)) as T;

const canonicalize = (value: unknown): string => {
  if (Array.isArray(value)) {
    return `[${value.map(canonicalize).join(",")}]`;
  }
  if (value !== null && typeof value === "object") {
    const entries = Object.entries(value as Record<string, unknown>)
      .filter(([, item]) => item !== undefined)
      .sort(([left], [right]) => left.localeCompare(right));
    return `{${entries
      .map(([key, item]) => `${JSON.stringify(key)}:${canonicalize(item)}`)
      .join(",")}}`;
  }
  return JSON.stringify(value ?? null);
};

const stringValue = (value: unknown): string =>
  typeof value === "string" ? value : "";

const numberValue = (value: unknown): number =>
  typeof value === "number" && Number.isFinite(value) ? value : 1;

const objectValue = (value: unknown): Record<string, unknown> =>
  value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};

const stringArrayValue = (value: unknown): string[] =>
  Array.isArray(value)
    ? value.filter((item): item is string => typeof item === "string")
    : [];
