import { createPinia, setActivePinia } from "pinia";
import { afterEach, describe, expect, it, vi } from "vitest";
import { documentApi, revisionApi } from "../../src/api";
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

describe("document copy", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("opens the copied document and keeps a late result from replacing selection", async () => {
    setActivePinia(createPinia());
    const store = useDocumentStore();
    setDocument(store, "doc-a", "A");
    const copy = deferred({
      documentId: "doc-copy",
      revision: 1,
      virtualPath: "A copy.md",
      committedAt: "2026-11-02T00:00:00Z",
      auditEventId: "audit",
    });
    vi.spyOn(documentApi, "copy").mockReturnValue(copy.promise);
    vi.spyOn(documentApi, "metadata").mockImplementation((id) =>
      Promise.resolve(metadata(id, id === "doc-a" ? "A" : "B")),
    );
    vi.spyOn(documentApi, "content").mockImplementation((id) =>
      Promise.resolve(content(id, id)),
    );
    vi.spyOn(documentApi, "map").mockImplementation((id) =>
      Promise.resolve(map(id, id === "doc-a" ? "A" : "B")),
    );

    const copying = store.copy({ title: "A copy" });
    await store.open("doc-b");
    copy.resolve({
      documentId: "doc-copy",
      revision: 1,
      virtualPath: "A copy.md",
      committedAt: "2026-11-02T00:00:00Z",
      auditEventId: "audit",
    });
    const result = await copying;

    expect(result.documentId).toBe("doc-copy");
    expect(store.documentId).toBe("doc-b");
  });
});

describe("document operation generation guards", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("drops stale conflict and reload responses after opening another document", async () => {
    setActivePinia(createPinia());
    const store = useDocumentStore();
    setDocument(store, "doc-a", "A");
    const conflictMetadata = deferred(metadata("doc-a", "A conflict"));
    const reloadMetadata = deferred(metadata("doc-a", "A reload"));
    let aMetadataCalls = 0;
    vi.spyOn(documentApi, "metadata").mockImplementation((id) => {
      if (id === "doc-b") {
        return Promise.resolve(metadata("doc-b", "B"));
      }
      aMetadataCalls++;
      return aMetadataCalls === 1
        ? conflictMetadata.promise
        : reloadMetadata.promise;
    });
    vi.spyOn(documentApi, "content").mockImplementation((id) =>
      Promise.resolve(content(id, id)),
    );
    vi.spyOn(documentApi, "map").mockImplementation((id) =>
      Promise.resolve(map(id, id === "doc-a" ? "A" : "B")),
    );

    const conflictLoad = store.loadLatestConflict();
    const reload = store.reloadLatest();
    await store.open("doc-b");

    conflictMetadata.resolve(metadata("doc-a", "A conflict"));
    reloadMetadata.resolve(metadata("doc-a", "A reload"));
    await Promise.all([conflictLoad, reload]);

    expect(store.documentId).toBe("doc-b");
    expect(store.draft?.title).toBe("B");
    expect(store.conflict).toBeNull();
  });

  it("invalidates an in-flight revision compare when selection changes", async () => {
    setActivePinia(createPinia());
    const store = useDocumentStore();
    setDocument(store, "doc-a", "A");
    store.selectedRevisionIds = ["rev-1", "rev-2"];
    const firstContent = deferred(revisionContent("doc-a", "rev-1", "one"));
    vi.spyOn(revisionApi, "content").mockImplementation((_id, revisionId) =>
      revisionId === "rev-1"
        ? firstContent.promise
        : Promise.resolve(revisionContent("doc-a", "rev-2", "two")),
    );

    const comparison = store.compareSelectedRevisions();
    store.toggleRevisionSelection("rev-2");
    firstContent.resolve(revisionContent("doc-a", "rev-1", "one"));
    await comparison;

    expect(store.revisionDiff).toBe("");
    expect(store.revisionCompareLoading).toBe(false);
    expect(store.revisionContents["rev-1"]).toBeDefined();
  });

  it("stops restore follow-up work after the document changes", async () => {
    setActivePinia(createPinia());
    const store = useDocumentStore();
    setDocument(store, "doc-a", "A");
    const restore = deferred<void>(undefined);
    vi.spyOn(revisionApi, "restore").mockReturnValue(restore.promise);
    vi.spyOn(documentApi, "metadata").mockImplementation((id) =>
      Promise.resolve(metadata(id, id === "doc-a" ? "A" : "B")),
    );
    vi.spyOn(documentApi, "content").mockImplementation((id) =>
      Promise.resolve(content(id, id)),
    );
    vi.spyOn(documentApi, "map").mockImplementation((id) =>
      Promise.resolve(map(id, id === "doc-a" ? "A" : "B")),
    );

    const restoring = store.restoreRevision("rev-0");
    await store.open("doc-b");
    restore.resolve(undefined);
    await restoring;

    expect(store.documentId).toBe("doc-b");
    expect(store.draft?.title).toBe("B");
  });

  it("does not continue reading chunks for an open that was superseded", async () => {
    setActivePinia(createPinia());
    const store = useDocumentStore();
    const firstChunk = deferred({
      documentId: "doc-a",
      revision: 1,
      content: "A",
      offset: 0,
      nextOffset: 1,
      totalLength: 2,
      done: false,
    });
    const contentCalls: string[] = [];
    vi.spyOn(documentApi, "metadata").mockImplementation((id) =>
      Promise.resolve(metadata(id, id === "doc-a" ? "A" : "B")),
    );
    vi.spyOn(documentApi, "content").mockImplementation((id, offset) => {
      contentCalls.push(`${id}:${offset}`);
      if (id === "doc-a") {
        return firstChunk.promise;
      }
      return Promise.resolve(content(id, "B"));
    });
    vi.spyOn(documentApi, "map").mockImplementation((id) =>
      Promise.resolve(map(id, id === "doc-a" ? "A" : "B")),
    );

    const openingA = store.open("doc-a");
    await Promise.resolve();
    await store.open("doc-b");
    firstChunk.resolve({
      documentId: "doc-a",
      revision: 1,
      content: "A",
      offset: 0,
      nextOffset: 1,
      totalLength: 2,
      done: false,
    });
    await openingA;

    expect(contentCalls.filter((value) => value.startsWith("doc-a:"))).toEqual([
      "doc-a:0",
    ]);
    expect(store.documentId).toBe("doc-b");
  });

  it("returns late create results without replacing the current document", async () => {
    setActivePinia(createPinia());
    const store = useDocumentStore();
    setDocument(store, "doc-a", "A");
    const create = deferred({
      documentId: "doc-created",
      revision: 1,
      virtualPath: "Created.md",
      committedAt: "2026-11-02T00:00:00Z",
      auditEventId: "audit",
    });
    vi.spyOn(documentApi, "create").mockReturnValue(create.promise);
    vi.spyOn(documentApi, "metadata").mockImplementation((id) =>
      Promise.resolve(metadata(id, id === "doc-a" ? "A" : "B")),
    );
    vi.spyOn(documentApi, "content").mockImplementation((id) =>
      Promise.resolve(content(id, id)),
    );
    vi.spyOn(documentApi, "map").mockImplementation((id) =>
      Promise.resolve(map(id, id === "doc-a" ? "A" : "B")),
    );

    const creating = store.create({
      title: "Created",
      virtualPath: "Created.md",
      format: "txt",
      content: "created",
      tags: [],
    });
    await store.open("doc-b");
    create.resolve({
      documentId: "doc-created",
      revision: 1,
      virtualPath: "Created.md",
      committedAt: "2026-11-02T00:00:00Z",
      auditEventId: "audit",
    });
    const result = await creating;

    expect(result.documentId).toBe("doc-created");
    expect(store.documentId).toBe("doc-b");
    expect(store.draft?.title).toBe("B");
  });
});

describe("document related knowledge", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("loads once per document and resets with sensitive state", async () => {
    setActivePinia(createPinia());
    const store = useDocumentStore();
    setDocument(store, "doc-a", "A");
    const pending = deferred({
      items: [relatedItem("related-a", "Related A")],
    });
    vi.spyOn(documentApi, "related").mockReturnValue(pending.promise);

    const first = store.loadRelated();
    const second = store.loadRelated();

    expect(documentApi.related).toHaveBeenCalledOnce();
    expect(store.relatedStatus).toBe("loading");
    pending.resolve({
      items: [relatedItem("related-a", "Related A")],
    });
    await Promise.all([first, second]);

    expect(store.relatedStatus).toBe("loaded");
    expect(store.relatedItems).toHaveLength(1);
    expect(store.relatedLoadedDocumentId).toBe("doc-a");

    store.resetSensitive();

    expect(store.relatedStatus).toBe("idle");
    expect(store.relatedItems).toEqual([]);
    expect(store.relatedError).toBeNull();
    expect(store.relatedLoadedDocumentId).toBeNull();
  });

  it("clears immediately and drops a late response after switching documents", async () => {
    setActivePinia(createPinia());
    const store = useDocumentStore();
    setDocument(store, "doc-a", "A");
    const pending = deferred({
      items: [relatedItem("old-related", "Old related")],
    });
    vi.spyOn(documentApi, "related").mockReturnValue(pending.promise);
    vi.spyOn(documentApi, "metadata").mockImplementation((id) =>
      Promise.resolve(metadata(id, id === "doc-a" ? "A" : "B")),
    );
    vi.spyOn(documentApi, "content").mockImplementation((id) =>
      Promise.resolve(content(id, id)),
    );
    vi.spyOn(documentApi, "map").mockImplementation((id) =>
      Promise.resolve(map(id, id === "doc-a" ? "A" : "B")),
    );

    const loading = store.loadRelated();
    await store.open("doc-b");
    expect(store.relatedStatus).toBe("idle");
    expect(store.relatedItems).toEqual([]);

    pending.resolve({
      items: [relatedItem("old-related", "Old related")],
    });
    await loading;

    expect(store.documentId).toBe("doc-b");
    expect(store.relatedStatus).toBe("idle");
    expect(store.relatedItems).toEqual([]);
  });

  it("force refresh replaces a loaded result", async () => {
    setActivePinia(createPinia());
    const store = useDocumentStore();
    setDocument(store, "doc-a", "A");
    vi.spyOn(documentApi, "related")
      .mockResolvedValueOnce({
        items: [relatedItem("related-a", "Related A")],
      })
      .mockResolvedValueOnce({
        items: [relatedItem("related-b", "Related B")],
      });

    await store.loadRelated();
    await store.loadRelated(true);

    expect(documentApi.related).toHaveBeenCalledTimes(2);
    expect(store.relatedItems[0]?.title).toBe("Related B");
  });
});

const deferred = <T>(initial?: T) => {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise;
  });
  if (initial !== undefined) {
    resolve(initial);
  }
  return {
    promise,
    resolve: (value: T) => resolve(value),
  };
};

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

const content = (documentId: string, value: string) => ({
  documentId,
  revision: 1,
  content: value,
  offset: 0,
  nextOffset: value.length,
  totalLength: value.length,
  done: true,
});

const revisionContent = (
  documentId: string,
  revisionId: string,
  contentValue: string,
) => ({
  revisionId,
  documentId,
  revision: 1,
  title: contentValue,
  virtualPath: `${contentValue}.md`,
  format: "md" as const,
  tags: [],
  content: contentValue,
  createdAt: "2026-11-02T00:00:00Z",
  retainUntil: "2026-11-03T00:00:00Z",
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

const relatedItem = (documentId: string, title: string) => ({
  documentId,
  title,
  virtualPath: `${title}.md`,
  revision: 1,
  tags: ["Work"],
  score: 40,
  reasons: [
    {
      code: "same_project" as const,
      label: "同一项目",
    },
  ],
  updatedAt: "2026-11-02T00:00:00Z",
});

const setDocument = (
  store: ReturnType<typeof useDocumentStore>,
  documentId: string,
  title: string,
) => {
  const info = metadata(documentId, title);
  store.documentId = documentId;
  store.metadata = info;
  store.loadedRevision = info.revision;
  const draft = {
    title,
    virtualPath: info.virtualPath,
    content: title,
    tags: [],
  };
  store.savedDraft = draft;
  store.draft = { ...draft };
};
