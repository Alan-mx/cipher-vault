import type { ElementDefinition } from "cytoscape";
import type { GraphEdge, GraphNode } from "../types/api";

export const toGraphElements = (
  nodes: GraphNode[],
  edges: GraphEdge[],
): ElementDefinition[] => [
  ...nodes.map((node) => ({
    data: {
      id: node.documentId,
      label: node.title,
      path: node.virtualPath,
      center: node.isCenter ? 1 : 0,
      depth: node.depth,
      degree: node.degree,
    },
  })),
  ...edges.map((edge) => ({
    data: {
      id:
        `${edge.edgeKind}:${edge.relationType ?? "link"}:` +
        `${edge.relationId ?? `${edge.sourceDocumentId}:${edge.targetDocumentId}`}`,
      source: edge.sourceDocumentId,
      target: edge.targetDocumentId,
      weight: edge.linkCount,
      edgeKind: edge.edgeKind,
      relationType: edge.relationType ?? "",
      label: edge.relationType ?? "",
      directed: edge.isDirected ? 1 : 0,
      arrowShape: edge.isDirected ? "triangle" : "none",
      lineStyle: edge.edgeKind === "typed_relation" ? "dashed" : "solid",
      lineColor: edge.edgeKind === "typed_relation" ? "#8a5a2b" : "#a3aca6",
    },
  })),
];
