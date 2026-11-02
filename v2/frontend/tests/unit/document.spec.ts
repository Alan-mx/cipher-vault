import { createPinia, setActivePinia } from "pinia";
import { afterEach, describe, expect, it, vi } from "vitest";
import { documentApi } from "../../src/api";
import { useDocumentStore } from "../../src/stores/document";
import { isGuid } from "../../src/utils/guid";

describe("document route validation", () => {
  it("accepts non-empty versioned GUIDs only", () => {
    expect(isGuid("00000000-0000-4000-8000-000000000001")).toBe(true);
    expect(isGuid("not-a-guid")).toBe(false);
    expect(isGuid("00000000-0000-0000-0000-000000000000")).toBe(false);
  });
});

describe("document inspector", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("ignores a late response from the previous document", async () => {
    setActivePinia(createPinia());
    const store = useDocumentStore();
    const metadata = (documentId: string, title: string) => ({
      documentId,
      title,
      virtualPath: `${title}.md`,
      format: "md" as const,
      revision: 1,
      tags: [],
      createdAt: "2026-11-02T00:00:00Z",
      updatedAt: "2026-11-02T00:00:00Z",
    });
    const map = (documentId: string, title: string) => ({
      metadata: metadata(documentId, title),
      headings: [],
      frontMatterFields: [],
      outgoingLinks: [],
      backlinkCount: 0,
      headingsTruncated: false,
      frontMatterTruncated: false,
      linksTruncated: false,
    });
    const documents = {
      "doc-a": metadata("doc-a", "A"),
      "doc-b": metadata("doc-b", "B"),
    };
    vi.spyOn(documentApi, "metadata").mockImplementation((id) =>
      Promise.resolve(documents[id as keyof typeof documents]!),
    );
    vi.spyOn(documentApi, "content").mockImplementation((id) =>
      Promise.resolve({
        documentId: id,
        revision: 1,
        content: id,
        offset: 0,
        nextOffset: id.length,
        totalLength: id.length,
        done: true,
      }),
    );
    vi.spyOn(documentApi, "map").mockImplementation((id) =>
      Promise.resolve(map(id, id === "doc-a" ? "A" : "B")),
    );
    let resolveBacklinks:
      | ((value: {
          items: Array<{
            sourceDocumentId: string;
            sourceTitle: string;
            sourceVirtualPath: string;
            sourceRevision: number;
            occurrenceCount: number;
            updatedAt: string;
          }>;
          nextCursor: null;
        }) => void)
      | null = null;
    vi.spyOn(documentApi, "backlinks").mockImplementation(
      () =>
        new Promise((resolve) => {
          resolveBacklinks = resolve;
        }),
    );

    await store.open("doc-a");
    const staleLoad = store.loadBacklinks();
    await store.open("doc-b");

    resolveBacklinks!({
      items: [
        {
          sourceDocumentId: "old-source",
          sourceTitle: "Old source",
          sourceVirtualPath: "old.md",
          sourceRevision: 1,
          occurrenceCount: 1,
          updatedAt: "2026-11-02T00:00:00Z",
        },
      ],
      nextCursor: null,
    });
    await staleLoad;

    expect(store.documentId).toBe("doc-b");
    expect(store.backlinks).toEqual([]);
  });

  it("keeps the committed revision when metadata refresh fails", async () => {
    setActivePinia(createPinia());
    const store = useDocumentStore();
    const initialMetadata = {
      documentId: "doc-a",
      title: "A",
      virtualPath: "A.md",
      format: "md" as const,
      revision: 1,
      tags: [],
      createdAt: "2026-11-02T00:00:00Z",
      updatedAt: "2026-11-02T00:00:00Z",
    };
    vi.spyOn(documentApi, "metadata")
      .mockResolvedValueOnce(initialMetadata)
      .mockRejectedValueOnce(new Error("refresh failed"));
    vi.spyOn(documentApi, "content").mockResolvedValue({
      documentId: "doc-a",
      revision: 1,
      content: "before",
      offset: 0,
      nextOffset: 6,
      totalLength: 6,
      done: true,
    });
    vi.spyOn(documentApi, "map").mockResolvedValue({
      metadata: initialMetadata,
      headings: [],
      frontMatterFields: [],
      outgoingLinks: [],
      backlinkCount: 0,
      headingsTruncated: false,
      frontMatterTruncated: false,
      linksTruncated: false,
    });
    vi.spyOn(documentApi, "update").mockResolvedValue({
      documentId: "doc-a",
      revision: 2,
      virtualPath: "A.md",
      committedAt: "2026-11-02T00:01:00Z",
      auditEventId: "audit",
    });

    await store.open("doc-a");
    store.updateDraft({ content: "after" });
    const result = await store.save();

    expect(result?.revision).toBe(2);
    expect(store.loadedRevision).toBe(2);
    expect(store.dirty).toBe(false);
    expect(store.conflict).toBeNull();
    expect(store.saveState).toBe("idle");
    expect(store.savedDraft?.content).toBe("after");
    expect(store.refreshWarning).toContain("已保存");
  });
});
