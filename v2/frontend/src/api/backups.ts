import { apiClient } from "./client";
import type {
  BackupCreateResponse,
  BackupListResponse,
  BackupQuery,
} from "../types/api";

export const backupApi = {
  create: (): Promise<BackupCreateResponse> =>
    apiClient.request<BackupCreateResponse>("/api/backups", {
      method: "POST",
      csrf: true,
    }),

  list: (query: BackupQuery = {}): Promise<BackupListResponse> =>
    apiClient.request<BackupListResponse>("/api/backups", {
      method: "GET",
      query: {
        pageSize: query.pageSize,
        cursor: query.cursor,
      },
    }),

  downloadUrl: (fileName: string): string =>
    `/api/backups/${encodeURIComponent(fileName)}/download`,
};
