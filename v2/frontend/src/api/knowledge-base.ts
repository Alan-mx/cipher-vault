import type {
  DashboardView,
  DraftDetail,
  DraftListItem,
  KnowledgeContentChunk,
  KnowledgeDetail,
  KnowledgeModel,
  KnowledgeSummary,
  PagedResult,
  SourceContentChunk,
  SourceDetail,
  SourceLocator,
  SourceLocatorContent,
  SourceRevision,
  SourceSummary,
} from "../types/knowledge-base";
import { toKnowledgeModelUpdateRequest } from "../utils/knowledge-model";
import { apiClient, type QueryInput } from "./client";

const key = () => crypto.randomUUID();

export const knowledgeBaseApi = {
  dashboard: () => apiClient.request<DashboardView>("/api/dashboard"),

  knowledgeModel: () =>
    apiClient.request<KnowledgeModel>("/api/knowledge-model"),
  replaceKnowledgeModel: (model: KnowledgeModel) =>
    apiClient.request<KnowledgeModel>("/api/knowledge-model", {
      method: "PUT",
      ifMatch: model.version,
      csrf: true,
      idempotencyKey: key(),
      body: toKnowledgeModelUpdateRequest(model),
    }),

  sources: (query: QueryInput = {}) =>
    apiClient.request<PagedResult<SourceSummary>>("/api/sources", { query }),
  source: (sourceId: string) =>
    apiClient.request<SourceDetail>(
      `/api/sources/${encodeURIComponent(sourceId)}`,
    ),
  sourceRevisions: (sourceId: string) =>
    apiClient.request<PagedResult<SourceRevision>>(
      `/api/sources/${encodeURIComponent(sourceId)}/revisions`,
    ),
  sourceContent: (sourceId: string, revision: number, offset = 0) =>
    apiClient.request<SourceContentChunk>(
      `/api/sources/${encodeURIComponent(sourceId)}/revisions/${revision}/content`,
      { query: { offset, maxChars: 32_768 } },
    ),
  sourceLocators: (sourceId: string, revision: number) =>
    apiClient.request<PagedResult<SourceLocator>>(
      `/api/sources/${encodeURIComponent(sourceId)}/revisions/${revision}/locators`,
      { query: { pageSize: 100 } },
    ),
  sourceLocatorContent: (
    sourceId: string,
    revision: number,
    locatorId: string,
    offset = 0,
  ) =>
    apiClient.request<SourceLocatorContent>(
      `/api/sources/${encodeURIComponent(sourceId)}/revisions/${revision}/locators/${encodeURIComponent(locatorId)}/content`,
      { query: { offset, maxChars: 32_768 } },
    ),

  knowledge: (query: QueryInput = {}) =>
    apiClient.request<PagedResult<KnowledgeSummary>>("/api/knowledge", {
      query,
    }),
  searchKnowledge: (body: Record<string, unknown>) =>
    apiClient.request<PagedResult<KnowledgeSummary & { revision: number }>>(
      "/api/knowledge/search",
      { method: "POST", body },
    ),
  knowledgeDetail: (knowledgeId: string) =>
    apiClient.request<KnowledgeDetail>(
      `/api/knowledge/${encodeURIComponent(knowledgeId)}`,
    ),
  knowledgeContent: (knowledgeId: string, revision: number) =>
    apiClient.request<KnowledgeContentChunk>(
      `/api/knowledge/${encodeURIComponent(knowledgeId)}/revisions/${revision}/content`,
    ),
  localGraph: (centerKnowledgeId: string) =>
    apiClient.request<{
      nodes: Array<{
        knowledgeId: string;
        title: string;
        degree: number;
        isCenter: boolean;
      }>;
      edges: unknown[];
      truncated: boolean;
    }>("/api/graph/local", {
      query: { centerKnowledgeId, depth: 2, maxNodes: 200, maxEdges: 400 },
    }),

  drafts: (query: QueryInput = {}) =>
    apiClient.request<PagedResult<DraftListItem>>("/api/drafts", { query }),
  draft: (draftId: string) =>
    apiClient.request<DraftDetail>(
      `/api/drafts/${encodeURIComponent(draftId)}`,
    ),
  createDraft: (request: Record<string, unknown>) =>
    apiClient.request<DraftListItem>("/api/drafts", {
      method: "POST",
      csrf: true,
      idempotencyKey: key(),
      body: request,
    }),
  submitDraft: (draftId: string, revision: number) =>
    draftAction(draftId, "submit-review", revision),
  discardDraft: (draftId: string, revision: number) =>
    draftAction(draftId, "discard", revision),
  applyDraft: (draftId: string, revision: number) =>
    apiClient.request("/api/drafts/" + encodeURIComponent(draftId) + "/apply", {
      method: "POST",
      csrf: true,
      idempotencyKey: key(),
      body: { expectedDraftRevision: revision },
    }),
  rejectDraft: (draftId: string, revision: number) =>
    draftAction(draftId, "reject", revision),
};

const draftAction = (
  draftId: string,
  action: string,
  expectedDraftRevision: number,
) =>
  apiClient.request(`/api/drafts/${encodeURIComponent(draftId)}/${action}`, {
    method: "POST",
    csrf: true,
    idempotencyKey: key(),
    body: { expectedDraftRevision },
  });
