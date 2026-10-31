import { ref } from "vue";
import { defineStore } from "pinia";
import { knowledgeApi } from "../api";
import { registerSensitiveStore } from "../session/lifecycle";
import type { GraphEdge, GraphNode, LocalGraphQuery } from "../types/api";
import { ApiClientError, StaleSessionResponseError } from "../types/domain";

export interface GraphFilters {
  depth: 1 | 2 | 3;
  tags: string[];
  folder: string;
  maxNodes: number;
  maxEdges: number;
}

export const useGraphStore = defineStore("graph", () => {
  const centerDocumentId = ref<string | null>(null);
  const filters = ref<GraphFilters>({
    depth: 2,
    tags: [],
    folder: "",
    maxNodes: 200,
    maxEdges: 400,
  });
  const nodes = ref<GraphNode[]>([]);
  const edges = ref<GraphEdge[]>([]);
  const truncated = ref(false);
  const loading = ref(false);
  const error = ref<ApiClientError | null>(null);
  let requestGeneration = 0;

  const buildQuery = (): LocalGraphQuery | null => {
    if (!centerDocumentId.value) {
      return null;
    }
    return {
      centerDocumentId: centerDocumentId.value,
      depth: filters.value.depth,
      ...(filters.value.tags.length ? { tags: filters.value.tags } : {}),
      ...(filters.value.folder.trim()
        ? { folder: filters.value.folder.trim() }
        : {}),
      maxNodes: filters.value.maxNodes,
      maxEdges: filters.value.maxEdges,
    };
  };

  const load = async (): Promise<void> => {
    const query = buildQuery();
    if (!query) {
      return;
    }
    const generation = ++requestGeneration;
    loading.value = true;
    error.value = null;
    try {
      const result = await knowledgeApi.graph(query);
      if (generation !== requestGeneration) {
        return;
      }
      nodes.value = result.nodes;
      edges.value = result.edges;
      truncated.value = result.truncated;
    } catch (caught) {
      if (
        generation === requestGeneration &&
        !(caught instanceof StaleSessionResponseError)
      ) {
        error.value =
          caught instanceof ApiClientError
            ? caught
            : new ApiClientError(0, {
                code: "network_error",
                message: "无法加载局部关系图。",
              });
      }
    } finally {
      if (generation === requestGeneration) {
        loading.value = false;
      }
    }
  };

  const setCenter = async (documentId: string): Promise<void> => {
    centerDocumentId.value = documentId;
    await load();
  };

  const setFilters = (update: Partial<GraphFilters>): void => {
    filters.value = { ...filters.value, ...update };
  };

  const resetSensitive = (): void => {
    requestGeneration++;
    centerDocumentId.value = null;
    filters.value = {
      depth: 2,
      tags: [],
      folder: "",
      maxNodes: 200,
      maxEdges: 400,
    };
    nodes.value = [];
    edges.value = [];
    truncated.value = false;
    loading.value = false;
    error.value = null;
  };

  registerSensitiveStore({ resetSensitive });

  return {
    centerDocumentId,
    filters,
    nodes,
    edges,
    truncated,
    loading,
    error,
    load,
    setCenter,
    setFilters,
    resetSensitive,
  };
});
