import { describe, expect, it } from "vitest";
import type { DocumentDraftDetail } from "../../src/types/api";
import { buildEditedUpdatePayload } from "../../src/utils/draft-payload";

describe("draft payload editing", () => {
  it("normalizes body operations while preserving other operations and context", () => {
    const detail: DocumentDraftDetail = {
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
      },
      payload: {
        operations: [
          { type: "append_body", content: "old body" },
          { type: "set_property", key: "status", value: "verified" },
          { type: "replace_body", content: "older body" },
          { type: "add_tag", tag: "Reviewed" },
        ],
        reviewContext: {
          summary: "AI summary",
          sources: [],
        },
      },
      reviewContext: {
        summary: "AI summary",
        sources: [],
      },
      preview: {
        content: "# Draft",
        tags: [],
        unifiedDiff: "",
        diffTruncated: false,
        operationCount: 4,
        structuredChanges: {
          changes: [],
          totalChangeCount: 0,
          truncated: false,
        },
        operationSummary: {
          total: 4,
          items: [],
        },
        bodyEdit: {
          canSave: true,
          reason: null,
        },
      },
    };

    expect(buildEditedUpdatePayload(detail, "# Changed")).toEqual({
      operations: [
        { type: "set_property", key: "status", value: "verified" },
        { type: "add_tag", tag: "Reviewed" },
        { type: "replace_body", content: "# Changed" },
      ],
      reviewContext: {
        summary: "AI summary",
        sources: [],
      },
    });
  });

  it("omits absent or cleared review context", () => {
    const detail = {
      payload: {
        operations: [],
        reviewContext: null,
      },
    } as unknown as DocumentDraftDetail;

    expect(buildEditedUpdatePayload(detail, "Body")).toEqual({
      operations: [{ type: "replace_body", content: "Body" }],
    });
  });
});
