import { describe, expect, it } from "vitest";
import { toGraphElements } from "../../src/utils/graph-elements";

describe("graph elements", () => {
  it("maps backend nodes and edges without inventing links", () => {
    const elements = toGraphElements(
      [
        {
          documentId: "00000000-0000-4000-8000-000000000001",
          title: "Center",
          virtualPath: "Center.md",
          revision: 1,
          tags: [],
          depth: 0,
          degree: 1,
          isCenter: true,
        },
        {
          documentId: "00000000-0000-4000-8000-000000000002",
          title: "Other",
          virtualPath: "Other.md",
          revision: 1,
          tags: [],
          depth: 1,
          degree: 1,
          isCenter: false,
        },
      ],
      [
        {
          sourceDocumentId: "00000000-0000-4000-8000-000000000001",
          targetDocumentId: "00000000-0000-4000-8000-000000000002",
          linkCount: 1,
        },
      ],
    );

    expect(elements).toHaveLength(3);
    expect(elements.filter((element) => "source" in element.data)).toHaveLength(
      1,
    );
  });
});
