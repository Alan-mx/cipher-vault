import type {
  DocumentWriteResult,
  RevisionContent,
  RevisionPage,
} from "../types/api";
import { apiClient } from "./client";

const revisionRoot = (documentId: string): string =>
  `/api/documents/${encodeURIComponent(documentId)}/revisions`;

export const revisionApi = {
  list: (documentId: string, cursor?: string, pageSize = 20) =>
    apiClient.request<RevisionPage>(revisionRoot(documentId), {
      query: { cursor, pageSize },
    }),

  content: (documentId: string, revisionId: string) =>
    apiClient.request<RevisionContent>(
      `${revisionRoot(documentId)}/${encodeURIComponent(revisionId)}/content`,
    ),

  restore: (documentId: string, revisionId: string, currentRevision: number) =>
    apiClient.request<DocumentWriteResult>(
      `${revisionRoot(documentId)}/${encodeURIComponent(revisionId)}/restore`,
      {
        method: "POST",
        csrf: true,
        ifMatch: currentRevision,
      },
    ),
};
