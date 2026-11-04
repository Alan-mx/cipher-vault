import { createPinia, setActivePinia } from "pinia";
import { afterEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../../src/api";
import { useKnowledgeViewStore } from "../../src/stores/knowledge-view";
import type { KnowledgeViewItem } from "../../src/types/api";

describe("knowledge view store", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("loads facets and clears them with sensitive state", async () => {
    setActivePinia(createPinia());
    const store = useKnowledgeViewStore();
    vi.spyOn(knowledgeApi, "viewFacets").mockResolvedValue({
      field: "project",
      items: [
        {
          key: "PROJECT A",
          label: "Project A",
          documentCount: 2,
          isUnset: false,
        },
        {
          key: null,
          label: "未设置",
          documentCount: 1,
          isUnset: true,
        },
      ],
      truncated: true,
    });

    await expect(store.loadFacets("project", false)).resolves.toBe(true);

    expect(store.facets).toHaveLength(2);
    expect(store.facetsTruncated).toBe(true);
    expect(store.facetsLoading).toBe(false);

    store.resetSensitive();

    expect(store.facets).toEqual([]);
    expect(store.facetsTruncated).toBe(false);
    expect(store.facetsError).toBeNull();
  });

  it("loads document pages and appends with the returned cursor", async () => {
    setActivePinia(createPinia());
    const store = useKnowledgeViewStore();
    vi.spyOn(knowledgeApi, "viewDocuments")
      .mockResolvedValueOnce({
        items: [item("doc-1", "First")],
        nextCursor: "next-1",
      })
      .mockResolvedValueOnce({
        items: [item("doc-2", "Second")],
        nextCursor: null,
      });

    await expect(
      store.loadDocuments("project", { facetKey: "PROJECT A" }, false),
    ).resolves.toBe(true);
    expect(store.documents.map((document) => document.title)).toEqual([
      "First",
    ]);
    expect(store.documentsNextCursor).toBe("next-1");

    await expect(
      store.loadDocuments("project", { facetKey: "PROJECT A" }, false, true),
    ).resolves.toBe(true);
    expect(store.documents.map((document) => document.title)).toEqual([
      "First",
      "Second",
    ]);
    expect(store.documentsNextCursor).toBeNull();
    expect(knowledgeApi.viewDocuments).toHaveBeenLastCalledWith(
      "project",
      expect.objectContaining({ cursor: "next-1", pageSize: 30 }),
      expect.any(AbortSignal),
    );
  });

  it("drops an old document response after a new selection", async () => {
    setActivePinia(createPinia());
    const store = useKnowledgeViewStore();
    let resolveFirst!: (value: {
      items: KnowledgeViewItem[];
      nextCursor: null;
    }) => void;
    vi.spyOn(knowledgeApi, "viewDocuments")
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            resolveFirst = resolve;
          }),
      )
      .mockResolvedValueOnce({
        items: [item("doc-new", "New")],
        nextCursor: null,
      });

    const oldLoad = store.loadDocuments("project", { facetKey: "OLD" }, false);
    const newLoad = store.loadDocuments("project", { facetKey: "NEW" }, false);
    await newLoad;
    resolveFirst({
      items: [item("doc-old", "Old")],
      nextCursor: null,
    });
    await oldLoad;

    expect(store.documents.map((document) => document.title)).toEqual(["New"]);
    expect(store.documentsError).toBeNull();
  });
});

const item = (documentId: string, title: string): KnowledgeViewItem => ({
  documentId,
  title,
  virtualPath: `${title}.md`,
  format: "md",
  revision: 1,
  tags: ["Work"],
  updatedAt: "2026-11-04T00:00:00Z",
});
