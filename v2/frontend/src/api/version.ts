import { apiClient } from "./client";
import type { VersionInfo } from "../types/api";

export const versionApi = {
  get: (): Promise<VersionInfo> =>
    apiClient.request<VersionInfo>("/api/version", {
      method: "GET",
    }),
};
