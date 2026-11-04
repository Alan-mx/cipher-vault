import type {
  DocumentRelationRetractResponse,
  DocumentRelationsResponse,
  RelationSuggestionApplyResponse,
  RelationSuggestionDetail,
  RelationSuggestionPage,
  RelationSuggestionQuery,
  RelationSuggestionRejectResponse,
  RelationSuggestionSummary,
} from "../types/api";
import { apiClient } from "./client";

const suggestionsPath = "/api/relation-suggestions";

export const relationSuggestionsApi = {
  summary: (signal?: AbortSignal) =>
    apiClient.request<RelationSuggestionSummary>(`${suggestionsPath}/summary`, {
      ...(signal ? { signal } : {}),
    }),

  list: (query: RelationSuggestionQuery, signal?: AbortSignal) =>
    apiClient.request<RelationSuggestionPage>(suggestionsPath, {
      query: {
        status: query.status,
        relationType: query.relationType,
        sourceDocumentId: query.sourceDocumentId,
        targetDocumentId: query.targetDocumentId,
        updatedAfter: query.updatedAfter,
        updatedBefore: query.updatedBefore,
        pageSize: query.pageSize,
        cursor: query.cursor,
      },
      ...(signal ? { signal } : {}),
    }),

  get: (suggestionId: string, signal?: AbortSignal) =>
    apiClient.request<RelationSuggestionDetail>(
      `${suggestionsPath}/${suggestionId}`,
      {
        ...(signal ? { signal } : {}),
      },
    ),

  apply: (
    suggestionId: string,
    expectedSuggestionRevision: number,
    idempotencyKey: string,
  ) =>
    apiClient.request<RelationSuggestionApplyResponse>(
      `${suggestionsPath}/${suggestionId}/apply`,
      {
        method: "POST",
        csrf: true,
        idempotencyKey,
        body: { expectedSuggestionRevision },
      },
    ),

  reject: (
    suggestionId: string,
    expectedSuggestionRevision: number,
    idempotencyKey: string,
  ) =>
    apiClient.request<RelationSuggestionRejectResponse>(
      `${suggestionsPath}/${suggestionId}/reject`,
      {
        method: "POST",
        csrf: true,
        idempotencyKey,
        body: { expectedSuggestionRevision },
      },
    ),

  documentRelations: (documentId: string, signal?: AbortSignal) =>
    apiClient.request<DocumentRelationsResponse>(
      `/api/documents/${documentId}/relations`,
      {
        ...(signal ? { signal } : {}),
      },
    ),

  retractRelation: (relationId: string, idempotencyKey: string) =>
    apiClient.request<DocumentRelationRetractResponse>(
      `/api/document-relations/${relationId}/retract`,
      {
        method: "POST",
        csrf: true,
        idempotencyKey,
      },
    ),
};
