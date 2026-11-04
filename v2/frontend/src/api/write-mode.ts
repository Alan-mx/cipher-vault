import type { McpWriteMode, McpWriteModeStatus } from "../types/api";
import { apiClient } from "./client";

const path = "/api/vault/session/mcp-write-mode";

export const writeModeApi = {
  get: () => apiClient.request<McpWriteModeStatus>(path),

  set: (mode: McpWriteMode, idempotencyKey: string) =>
    apiClient.request<McpWriteModeStatus>(path, {
      method: "PUT",
      csrf: true,
      idempotencyKey,
      body: { mode },
    }),
};
