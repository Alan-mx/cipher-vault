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
          edgeKind: "explicit_link",
          linkCount: 1,
          relationId: null,
          relationType: null,
          isDirected: true,
        },
      ],
    );

    expect(elements).toHaveLength(3);
    expect(elements.filter((element) => "source" in element.data)).toHaveLength(
      1,
    );
    expect(elements[2]?.data.arrowShape).toBe("triangle");
    expect(elements[2]?.data.lineStyle).toBe("solid");
  });

  it("distinguishes symmetric typed relations from directed ones", () => {
    const nodes = [
      {
        documentId: "00000000-0000-4000-8000-000000000001",
        title: "Center",
        virtualPath: "Center.md",
        revision: 1,
        tags: [],
        depth: 0,
        degree: 2,
        isCenter: true,
      },
      {
        documentId: "00000000-0000-4000-8000-000000000002",
        title: "Other",
        virtualPath: "Other.md",
        revision: 1,
        tags: [],
        depth: 1,
        degree: 2,
        isCenter: false,
      },
    ];
    const elements = toGraphElements(nodes, [
      {
        sourceDocumentId: nodes[0]!.documentId,
        targetDocumentId: nodes[1]!.documentId,
        edgeKind: "typed_relation",
        linkCount: 1,
        relationId: "00000000-0000-4000-8000-000000000010",
        relationType: "depends_on",
        isDirected: true,
      },
      {
        sourceDocumentId: nodes[0]!.documentId,
        targetDocumentId: nodes[1]!.documentId,
        edgeKind: "typed_relation",
        linkCount: 1,
        relationId: "00000000-0000-4000-8000-000000000011",
        relationType: "related",
        isDirected: false,
      },
    ]);

    const edges = elements.filter((element) => "source" in element.data);
    expect(edges.map((edge) => edge.data.id)).toEqual([
      "typed_relation:depends_on:00000000-0000-4000-8000-000000000010",
      "typed_relation:related:00000000-0000-4000-8000-000000000011",
    ]);
    expect(edges[0]?.data.arrowShape).toBe("triangle");
    expect(edges[1]?.data.arrowShape).toBe("none");
    expect(edges.every((edge) => edge.data.lineStyle === "dashed")).toBe(true);
  });
});
