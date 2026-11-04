import type { McpWriteModeStatus } from "../types/api";

export type WriteModeDisplayState = "loading" | "draft" | "direct" | "unknown";

export const writeModeDisplayState = (
  status: McpWriteModeStatus | null,
  loading: boolean,
  hasError: boolean,
): WriteModeDisplayState => {
  if (loading && !hasError) {
    return "loading";
  }
  if (hasError || !status) {
    return "unknown";
  }
  return status.mode;
};
