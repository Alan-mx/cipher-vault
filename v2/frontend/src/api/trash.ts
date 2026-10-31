import type {
  DocumentWriteResult,
  PurgeResult,
  TrashPage,
  TrashRestoreRequest,
} from "../types/api";
import { apiClient } from "./client";

export const trashApi = {
  list: (cursor?: string, pageSize = 20) =>
    apiClient.request<TrashPage>("/api/trash", {
      query: { cursor, pageSize },
    }),

  restore: (
    documentId: string,
    revision: number,
    request: TrashRestoreRequest = {},
  ) =>
    apiClient.request<DocumentWriteResult>(
      `/api/trash/${encodeURIComponent(documentId)}/restore`,
      {
        method: "POST",
        csrf: true,
        ifMatch: revision,
        body: request,
      },
    ),

  purge: (documentId: string, revision: number) =>
    apiClient.request<PurgeResult>(
      `/api/trash/${encodeURIComponent(documentId)}/purge`,
      {
        method: "POST",
        csrf: true,
        ifMatch: revision,
      },
    ),
};
