import type {
  CreateDocumentDraftRequest,
  DocumentDraftApplyResult,
  DocumentDraftDetail,
  DocumentDraftMutationResult,
  DocumentDraftPage,
  DocumentDraftQuery,
  DocumentDraftStatus,
  UpdateDocumentDraftRequest,
} from "../types/api";
import { apiClient } from "./client";

const draftPath = (draftId: string): string =>
  `/api/drafts/${encodeURIComponent(draftId)}`;

export const draftsApi = {
  list: (query: DocumentDraftQuery = {}) =>
    apiClient.request<DocumentDraftPage>("/api/drafts", {
      query: {
        status: query.status,
        type: query.type,
        targetDocumentId: query.targetDocumentId,
        pageSize: query.pageSize,
        cursor: query.cursor,
      },
    }),

  get: (draftId: string) =>
    apiClient.request<DocumentDraftDetail>(draftPath(draftId)),

  create: (request: CreateDocumentDraftRequest, idempotencyKey: string) =>
    apiClient.request<DocumentDraftMutationResult>("/api/drafts", {
      method: "POST",
      csrf: true,
      idempotencyKey,
      body: request,
    }),

  update: (
    draftId: string,
    request: UpdateDocumentDraftRequest,
    idempotencyKey: string,
  ) =>
    apiClient.request<DocumentDraftMutationResult>(draftPath(draftId), {
      method: "PUT",
      csrf: true,
      idempotencyKey,
      body: request,
    }),

  rebase: (
    draftId: string,
    expectedDraftRevision: number,
    observedDocumentRevision: number,
    idempotencyKey: string,
  ) =>
    apiClient.request<DocumentDraftMutationResult>(
      `${draftPath(draftId)}/rebase`,
      {
        method: "POST",
        csrf: true,
        idempotencyKey,
        body: { expectedDraftRevision, observedDocumentRevision },
      },
    ),

  submitReview: (
    draftId: string,
    expectedDraftRevision: number,
    idempotencyKey: string,
  ) =>
    revisionAction(
      draftId,
      "submit-review",
      expectedDraftRevision,
      idempotencyKey,
    ),

  reject: (
    draftId: string,
    expectedDraftRevision: number,
    idempotencyKey: string,
  ) => revisionAction(draftId, "reject", expectedDraftRevision, idempotencyKey),

  discard: (
    draftId: string,
    expectedDraftRevision: number,
    idempotencyKey: string,
  ) =>
    revisionAction(draftId, "discard", expectedDraftRevision, idempotencyKey),

  apply: (
    draftId: string,
    expectedDraftRevision: number,
    idempotencyKey: string,
  ) =>
    apiClient.request<DocumentDraftApplyResult>(`${draftPath(draftId)}/apply`, {
      method: "POST",
      csrf: true,
      idempotencyKey,
      body: { expectedDraftRevision },
    }),
};

const revisionAction = (
  draftId: string,
  action: string,
  expectedDraftRevision: number,
  idempotencyKey: string,
) =>
  apiClient.request<DocumentDraftMutationResult>(
    `${draftPath(draftId)}/${action}`,
    {
      method: "POST",
      csrf: true,
      idempotencyKey,
      body: { expectedDraftRevision },
    },
  );

export type { DocumentDraftStatus };
