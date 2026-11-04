import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { draftsApi } from "../../src/api/drafts";
import { writeModeApi } from "../../src/api/write-mode";
import { useDocumentDraftsStore } from "../../src/stores/drafts";
import { useWriteModeStore } from "../../src/stores/write-mode";
import { resetSessionLifecycleForTest } from "../../src/session/lifecycle";
import { ApiClientError } from "../../src/types/domain";

describe("MCP write mode store", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  afterEach(() => {
    vi.restoreAllMocks();
    resetSessionLifecycleForTest();
  });

  it("loads and changes the session write mode with an idempotency key", async () => {
    const writeMode = useWriteModeStore();
    vi.spyOn(writeModeApi, "get").mockResolvedValue({
      mode: "draft",
      sessionId: "11111111-1111-1111-1111-111111111111",
      sessionGeneration: 1,
      updatedAt: "2026-11-04T00:00:00Z",
      effective: true,
    });
    const set = vi.spyOn(writeModeApi, "set").mockResolvedValue({
      mode: "direct",
      sessionId: "11111111-1111-1111-1111-111111111111",
      sessionGeneration: 1,
      updatedAt: "2026-11-04T00:00:01Z",
      effective: true,
    });

    await writeMode.load();
    expect(writeMode.status?.mode).toBe("draft");

    await writeMode.setMode("direct");

    expect(writeMode.status?.mode).toBe("direct");
    expect(set).toHaveBeenCalledOnce();
    expect(set.mock.calls[0]![1]).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
  });
});

describe("document drafts store", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  afterEach(() => {
    vi.restoreAllMocks();
    resetSessionLifecycleForTest();
  });

  it("saves an edited draft using the current revision and an idempotency key", async () => {
    const drafts = useDocumentDraftsStore();
    drafts.selected = {
      metadata: {
        draftId: "22222222-2222-4222-8222-222222222222",
        draftRevision: 3,
        draftType: "update",
        status: "pending_review",
        targetDocumentId: "33333333-3333-4333-8333-333333333333",
        baseDocumentRevision: 2,
        sourceActor: "mcp",
        title: "Draft",
        virtualPath: "notes/draft.md",
        format: "md",
        createdAt: "2026-11-04T00:00:00Z",
        updatedAt: "2026-11-04T00:00:00Z",
        terminalAt: null,
        appliedDocumentId: null,
        appliedDocumentRevision: null,
      },
      payload: { operations: [] },
      reviewContext: null,
      preview: {
        content: "# Draft",
        tags: [],
        unifiedDiff: "",
        diffTruncated: false,
        operationCount: 0,
        structuredChanges: {
          changes: [],
          totalChangeCount: 0,
          truncated: false,
        },
        operationSummary: {
          total: 0,
          items: [],
        },
        bodyEdit: {
          canSave: true,
          reason: null,
        },
      },
    };
    const update = vi.spyOn(draftsApi, "update").mockResolvedValue({
      draftId: "22222222-2222-4222-8222-222222222222",
      draftRevision: 4,
      status: "draft",
      updatedAt: "2026-11-04T00:00:01Z",
    });
    vi.spyOn(draftsApi, "list").mockResolvedValue({
      items: [],
      nextCursor: null,
    });
    vi.spyOn(draftsApi, "summary").mockResolvedValue({
      pendingReview: 1,
      working: 0,
      history: 0,
    });
    vi.spyOn(draftsApi, "get").mockResolvedValue(drafts.selected);

    const saved = await drafts.save({
      operations: [{ type: "replace_body", content: "# Changed" }],
    });

    expect(saved).toBe(true);
    expect(update).toHaveBeenCalledOnce();
    expect(update.mock.calls[0]![1]).toEqual({
      expectedDraftRevision: 3,
      payload: {
        operations: [{ type: "replace_body", content: "# Changed" }],
      },
    });
    expect(update.mock.calls[0]![2]).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
  });

  it("reuses a batch idempotency key after conflict and removes successful items", async () => {
    const drafts = useDocumentDraftsStore();
    drafts.items = [
      {
        draftId: "44444444-4444-4444-8444-444444444444",
        draftRevision: 2,
        draftType: "update",
        status: "draft",
        targetDocumentId: "55555555-5555-4555-8555-555555555555",
        baseDocumentRevision: 1,
        sourceActor: "mcp",
        title: "Batch draft",
        virtualPath: "notes/batch.md",
        format: "md",
        createdAt: "2026-11-04T00:00:00Z",
        updatedAt: "2026-11-04T00:00:00Z",
      },
    ];
    drafts.selectedDraftIds = ["44444444-4444-4444-8444-444444444444"];
    vi.spyOn(draftsApi, "summary").mockResolvedValue({
      pendingReview: 0,
      working: 1,
      history: 0,
    });
    vi.spyOn(draftsApi, "list").mockResolvedValue({
      items: drafts.items,
      nextCursor: null,
    });
    const submit = vi
      .spyOn(draftsApi, "submitReview")
      .mockRejectedValueOnce(
        new ApiClientError(409, {
          code: "draft_revision_conflict",
          message: "Revision changed.",
        }),
      )
      .mockResolvedValueOnce({
        draftId: "44444444-4444-4444-8444-444444444444",
        draftRevision: 3,
        status: "pending_review",
        updatedAt: "2026-11-04T00:00:01Z",
      });

    await drafts.runBatch("submit_review");
    expect(
      drafts.batchResults.get("44444444-4444-4444-8444-444444444444")?.kind,
    ).toBe("revision_conflict");
    expect(drafts.selectedDraftIds).toHaveLength(1);

    await drafts.runBatch("submit_review");

    expect(submit).toHaveBeenCalledTimes(2);
    expect(submit.mock.calls[0]![2]).toBe(submit.mock.calls[1]![2]);
    expect(drafts.selectedDraftIds).toHaveLength(0);
    expect(
      drafts.batchResults.get("44444444-4444-4444-8444-444444444444")?.kind,
    ).toBe("success");
  });
});
