import { createPinia, setActivePinia } from "pinia";
import { afterEach, describe, expect, it, vi } from "vitest";
import { relationSuggestionsApi } from "../../src/api";
import { useDocumentRelationsStore } from "../../src/stores/document-relations";
import { useRelationReviewsStore } from "../../src/stores/relation-reviews";
import { resetSessionLifecycleForTest } from "../../src/session/lifecycle";
import type {
  DocumentRelationItem,
  RelationSuggestionDetail,
  RelationSuggestionListItem,
} from "../../src/types/api";

const item: RelationSuggestionListItem = {
  suggestionId: "00000000-0000-4000-8000-000000000001",
  suggestionRevision: 2,
  relationType: "depends_on",
  status: "pending_review",
  sourceDocumentRevision: 3,
  targetDocumentRevision: 4,
  sourceDocument: {
    documentId: "00000000-0000-4000-8000-000000000010",
    title: "Source",
    virtualPath: "source.md",
    revision: 3,
  },
  targetDocument: {
    documentId: "00000000-0000-4000-8000-000000000011",
    title: "Target",
    virtualPath: "target.md",
    revision: 4,
  },
  sourceActor: "mcp",
  relationId: null,
  applyResult: null,
  staleReasonCode: null,
  createdAt: "2026-11-05T00:00:00Z",
  updatedAt: "2026-11-05T00:00:00Z",
  terminalAt: null,
};

const detail: RelationSuggestionDetail = {
  metadata: item,
  reason: "Reason",
  modelName: "model",
  agentName: null,
  clientName: null,
  evidence: [],
  appliedAt: null,
};

describe("relation review stores", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    resetSessionLifecycleForTest();
  });

  it("loads summary and list and applies with suggestion revision", async () => {
    setActivePinia(createPinia());
    const store = useRelationReviewsStore();
    vi.spyOn(relationSuggestionsApi, "summary").mockResolvedValue({
      pendingReview: 1,
      stale: 0,
      applied: 0,
      rejected: 0,
      expired: 0,
    });
    const list = vi.spyOn(relationSuggestionsApi, "list").mockResolvedValue({
      items: [item],
      nextCursor: null,
    });
    vi.spyOn(relationSuggestionsApi, "get").mockResolvedValue(detail);
    const apply = vi.spyOn(relationSuggestionsApi, "apply").mockResolvedValue({
      suggestionId: item.suggestionId,
      suggestionRevision: 3,
      status: "applied",
      relationId: "00000000-0000-4000-8000-000000000020",
      applyResult: "inserted",
      appliedAt: "2026-11-05T00:00:00Z",
    });

    await store.refresh();
    await store.select(item.suggestionId);
    expect(store.selected?.metadata.suggestionId).toBe(item.suggestionId);

    expect(await store.applySelected()).toBe(true);

    expect(list).toHaveBeenCalledTimes(2);
    expect(apply).toHaveBeenCalledWith(
      item.suggestionId,
      2,
      expect.stringMatching(
        /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
      ),
    );
    expect(store.selected).toBeNull();
  });

  it("retracts confirmed relations and clears sensitive state", async () => {
    setActivePinia(createPinia());
    const store = useDocumentRelationsStore();
    const relation: DocumentRelationItem = {
      relationId: "00000000-0000-4000-8000-000000000020",
      relationType: "depends_on",
      isDirected: true,
      direction: "outgoing",
      relatedDocument: item.targetDocument,
      createdAt: "2026-11-05T00:00:00Z",
      updatedAt: "2026-11-05T00:00:00Z",
    };
    vi.spyOn(relationSuggestionsApi, "documentRelations")
      .mockResolvedValueOnce({
        documentId: item.sourceDocument.documentId,
        items: [relation],
        truncated: false,
      })
      .mockResolvedValueOnce({
        documentId: item.sourceDocument.documentId,
        items: [],
        truncated: false,
      });
    const retract = vi
      .spyOn(relationSuggestionsApi, "retractRelation")
      .mockResolvedValue({
        relationId: relation.relationId,
        status: "retracted",
        retractedAt: "2026-11-05T00:00:01Z",
        auditEventId: "00000000-0000-4000-8000-000000000030",
      });

    await store.load(item.sourceDocument.documentId);
    expect(store.items).toHaveLength(1);
    expect(await store.retract(relation.relationId)).toBe(true);

    expect(retract).toHaveBeenCalledOnce();
    expect(store.items).toEqual([]);

    store.resetSensitive();
    expect(store.documentId).toBeNull();
  });
});
