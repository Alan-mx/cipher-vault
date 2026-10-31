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
      id: `${edge.sourceDocumentId}:${edge.targetDocumentId}`,
      source: edge.sourceDocumentId,
      target: edge.targetDocumentId,
      weight: edge.linkCount,
    },
  })),
];
