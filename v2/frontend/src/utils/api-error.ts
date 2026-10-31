import { ApiClientError } from "../types/domain";

export const describeApiError = (error: unknown): string => {
  if (error instanceof ApiClientError) {
    if (
      error.code === "unlock_rate_limited" &&
      error.retryAfterSeconds !== undefined
    ) {
      return `${error.message} 请等待 ${error.retryAfterSeconds} 秒后重试。`;
    }
    return error.message;
  }
  return "操作失败，请稍后重试。";
};
