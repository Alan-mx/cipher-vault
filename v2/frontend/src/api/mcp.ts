import type { McpInfo } from "../types/api";
import { apiClient } from "./client";

export const mcpApi = {
  info: () => apiClient.request<McpInfo>("/api/mcp/info"),
};
