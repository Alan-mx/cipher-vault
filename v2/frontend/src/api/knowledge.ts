import type {
  LocalGraphQuery,
  LocalGraphResult,
  KnowledgeFacetPage,
  KnowledgeViewDocumentQuery,
  KnowledgeViewField,
  KnowledgeViewPage,
  SearchPage,
  SearchQuery,
} from "../types/api";
import { apiClient } from "./client";

export const knowledgeApi = {
  search: (query: SearchQuery, signal?: AbortSignal) =>
    apiClient.request<SearchPage>("/api/search/query", {
      method: "POST",
      body: {
        query: query.query,
        searchMode: query.searchMode,
        tags: query.tags,
        format: query.format,
        updatedAfter: query.updatedAfter,
        updatedBefore: query.updatedBefore,
        hasBacklinks: query.hasBacklinks,
        orphanOnly: query.orphanOnly,
        propertyMatch: query.propertyMatch,
        propertyFilters: query.propertyFilters,
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

  viewFacets: (
    field: KnowledgeViewField,
    activeOnly: boolean,
    signal?: AbortSignal,
  ) =>
    apiClient.request<KnowledgeFacetPage>(
      `/api/knowledge/views/${field}/facets`,
      {
        query: {
          activeOnly: field === "project" ? activeOnly : undefined,
        },
        ...(signal ? { signal } : {}),
      },
    ),

  viewDocuments: (
    field: KnowledgeViewField,
    query: KnowledgeViewDocumentQuery,
    signal?: AbortSignal,
  ) =>
    apiClient.request<KnowledgeViewPage>(
      `/api/knowledge/views/${field}/documents`,
      {
        query: {
          facetKey: query.facetKey,
          unset: query.unset ? "true" : undefined,
          activeOnly: field === "project" ? query.activeOnly : undefined,
          pageSize: query.pageSize,
          cursor: query.cursor,
        },
        ...(signal ? { signal } : {}),
      },
    ),
};
