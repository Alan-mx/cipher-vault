import { createPinia, setActivePinia } from "pinia";
import { afterEach, describe, expect, it, vi } from "vitest";
import { documentApi, knowledgeApi } from "../../src/api";
import { resetSessionLifecycleForTest } from "../../src/session/lifecycle";
import { useKnowledgeMaturityQueueStore } from "../../src/stores/knowledge-maturity-queue";
import { ApiClientError } from "../../src/types/domain";
import type { KnowledgeMaturityQueueItem } from "../../src/types/api";

const item: KnowledgeMaturityQueueItem = {
  documentId: "00000000-0000-4000-8000-000000000001",
  title: "Raw Note",
  virtualPath: "notes/raw.md",
  format: "md",
  revision: 4,
  tags: ["Work"],
  updatedAt: "2026-11-05T00:00:00Z",
  maturityValue: " RAW ",
  maturityValueType: "string",
  maturityBucket: "pending_organization",
  nextMaturity: "excerpt",
};

describe("knowledge maturity queue store", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    resetSessionLifecycleForTest();
  });

  it("loads a bucket and patches the semantic next maturity", async () => {
    setActivePinia(createPinia());
    const store = useKnowledgeMaturityQueueStore();
    const list = vi.spyOn(knowledgeApi, "maturityQueue").mockResolvedValue({
      items: [item],
      nextCursor: null,
    });
    const patch = vi.spyOn(documentApi, "patch").mockResolvedValue({
      documentId: item.documentId,
      previousRevision: 4,
      revision: 5,
      dryRun: false,
      changed: true,
      unifiedDiff: "",
      diffTruncated: false,
      auditEventId: "00000000-0000-4000-8000-000000000010",
    });

    await store.load("pending_organization");
    expect(store.items).toEqual([item]);
    expect(await store.advance(item)).toBe(true);

    expect(patch).toHaveBeenCalledWith(item.documentId, 4, {
      targetKind: "front_matter",
      frontMatterKey: "maturity",
      operation: "set",
      value: "excerpt",
      dryRun: false,
    });
    expect(list).toHaveBeenCalledTimes(2);
  });

  it("avoids semantic no-ops and disables TXT actions", async () => {
    setActivePinia(createPinia());
    const store = useKnowledgeMaturityQueueStore();
    vi.spyOn(knowledgeApi, "maturityQueue").mockResolvedValue({
      items: [],
      nextCursor: null,
    });
    const patch = vi.spyOn(documentApi, "patch");

    expect(await store.setLevel(item, "raw")).toBe(true);
    expect(patch).not.toHaveBeenCalled();

    const textItem: KnowledgeMaturityQueueItem = {
      ...item,
      documentId: "00000000-0000-4000-8000-000000000002",
      format: "txt",
      maturityValue: null,
      maturityValueType: "missing",
      nextMaturity: "raw",
    };
    expect(await store.setLevel(textItem, "raw")).toBe(false);
    expect(await store.clear(textItem)).toBe(true);
    expect(patch).not.toHaveBeenCalled();
  });

  it("keeps the queue and exposes revision conflicts", async () => {
    setActivePinia(createPinia());
    const store = useKnowledgeMaturityQueueStore();
    vi.spyOn(knowledgeApi, "maturityQueue").mockResolvedValue({
      items: [item],
      nextCursor: null,
    });
    vi.spyOn(documentApi, "patch").mockRejectedValue(
      new ApiClientError(409, {
        code: "revision_conflict",
        message: "Document revision changed.",
        details: { currentRevision: 8 },
        correlationId: "test",
      }),
    );

    await store.load("pending_organization");
    expect(await store.advance(item)).toBe(false);
    expect(store.items).toEqual([item]);
    expect(store.actionError?.code).toBe("revision_conflict");
    expect(store.actionError?.message).toContain("请刷新队列");
  });
});
