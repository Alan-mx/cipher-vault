import type {
  LocalGraphQuery,
  LocalGraphResult,
  SearchPage,
  SearchQuery,
} from "../types/api";
import { apiClient } from "./client";

export const knowledgeApi = {
  search: (query: SearchQuery, signal?: AbortSignal) =>
    apiClient.request<SearchPage>("/api/search", {
      query: {
        query: query.query,
        tags: query.tags,
        format: query.format,
        updatedAfter: query.updatedAfter,
        updatedBefore: query.updatedBefore,
        hasBacklinks: query.hasBacklinks,
        orphanOnly: query.orphanOnly,
        pageSize: query.pageSize,
        cursor: query.cursor,
      },
      ...(signal ? { signal } : {}),
    }),

  graph: (query: LocalGraphQuery) =>
    apiClient.request<LocalGraphResult>("/api/graph/local", {
      query: {
        centerDocumentId: query.centerDocumentId,
        depth: query.depth,
        tags: query.tags,
        folder: query.folder,
        maxNodes: query.maxNodes,
        maxEdges: query.maxEdges,
      },
    }),
};
