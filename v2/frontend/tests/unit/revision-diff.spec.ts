import { describe, expect, it } from "vitest";
import type { RevisionContent } from "../../src/types/api";
import { createRevisionDiff } from "../../src/utils/revision-diff";

const revision = (
  revisionNumber: number,
  content: string,
): RevisionContent => ({
  documentId: "00000000-0000-4000-8000-000000000001",
  revisionId: `00000000-0000-4000-8000-00000000000${revisionNumber}`,
  revision: revisionNumber,
  title: "Note",
  virtualPath: "Note.md",
  format: "md",
  tags: [],
  content,
  createdAt: "2026-10-31T00:00:00Z",
  retainUntil: "2027-01-29T00:00:00Z",
});

describe("revision diff", () => {
  it("renders a unified patch from two immutable snapshots", () => {
    const patch = createRevisionDiff(
      revision(1, "old line\n"),
      revision(2, "new line\n"),
    );

    expect(patch).toContain("-old line");
    expect(patch).toContain("+new line");
  });
});
