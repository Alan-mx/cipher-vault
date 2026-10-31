import type { McpTokenRotateResponse, McpTokenStatus } from "../types/api";
import { apiClient } from "./client";

export const settingsApi = {
  mcpStatus: () => apiClient.request<McpTokenStatus>("/api/mcp/token/status"),

  rotateMcpToken: () =>
    apiClient.request<McpTokenRotateResponse>("/api/mcp/token/rotate", {
      method: "POST",
      csrf: true,
    }),
};
