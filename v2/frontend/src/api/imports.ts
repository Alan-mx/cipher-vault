import type {
  ImportCommitResult,
  ImportPreviewRequest,
  ImportPreviewResult,
} from "../types/api";
import { apiClient } from "./client";

export const importApi = {
  preview: (request: ImportPreviewRequest) =>
    apiClient.request<ImportPreviewResult>("/api/imports/preview", {
      method: "POST",
      csrf: true,
      body: request,
    }),

  commit: (token: string) =>
    apiClient.request<ImportCommitResult>("/api/imports/commit", {
      method: "POST",
      csrf: true,
      body: { token },
    }),
};
