import type {
  BacklinkPage,
  CopyDocumentRequest,
  CreateDocumentRequest,
  DocumentContentChunk,
  DocumentMap,
  DocumentMetadata,
  DocumentWriteResult,
  PatchDocumentRequest,
  PatchResult,
  RelatedDocumentsResponse,
  TrashDeleteResult,
  UnlinkedMentionPage,
  UpdateDocumentRequest,
} from "../types/api";
import { apiClient } from "./client";

const documentPath = (documentId: string): string =>
  `/api/documents/${encodeURIComponent(documentId)}`;

export const documentApi = {
  create: (request: CreateDocumentRequest) =>
    apiClient.request<DocumentWriteResult>("/api/documents", {
      method: "POST",
      csrf: true,
      body: request,
    }),

  copy: (
    documentId: string,
    revision: number,
    request: CopyDocumentRequest,
    idempotencyKey: string,
  ) =>
    apiClient.request<DocumentWriteResult>(`${documentPath(documentId)}/copy`, {
      method: "POST",
      csrf: true,
      ifMatch: revision,
      idempotencyKey,
      body: request,
    }),

  metadata: (documentId: string) =>
    apiClient.request<DocumentMetadata>(documentPath(documentId)),

  content: (documentId: string, offset = 0, maxChars = 32_768) =>
    apiClient.request<DocumentContentChunk>(
      `${documentPath(documentId)}/content`,
      { query: { offset, maxChars } },
    ),

  map: (documentId: string) =>
    apiClient.request<DocumentMap>(`${documentPath(documentId)}/map`),

  update: (
    documentId: string,
    revision: number,
    request: UpdateDocumentRequest,
  ) =>
    apiClient.request<DocumentWriteResult>(documentPath(documentId), {
      method: "PUT",
      csrf: true,
      ifMatch: revision,
      body: request,
    }),

  patch: (
    documentId: string,
    revision: number,
    request: PatchDocumentRequest,
  ) =>
    apiClient.request<PatchResult>(documentPath(documentId), {
      method: "PATCH",
      csrf: true,
      ifMatch: revision,
      body: request,
    }),

  delete: (documentId: string, revision: number) =>
    apiClient.request<TrashDeleteResult>(documentPath(documentId), {
      method: "DELETE",
      csrf: true,
      ifMatch: revision,
    }),

  backlinks: (documentId: string, cursor?: string, pageSize = 20) =>
    apiClient.request<BacklinkPage>(`${documentPath(documentId)}/backlinks`, {
      query: { cursor, pageSize },
    }),

  unlinkedMentions: (documentId: string, cursor?: string, pageSize = 20) =>
    apiClient.request<UnlinkedMentionPage>(
      `${documentPath(documentId)}/unlinked-mentions`,
      { query: { cursor, pageSize } },
    ),

  related: (documentId: string, limit = 10) =>
    apiClient.request<RelatedDocumentsResponse>(
      `${documentPath(documentId)}/related`,
      { query: { limit } },
    ),
};
