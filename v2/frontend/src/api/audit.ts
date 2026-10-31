import type { AuditPage, AuditQuery } from "../types/api";
import { apiClient } from "./client";

export const auditApi = {
  list: (query: AuditQuery = {}) =>
    apiClient.request<AuditPage>("/api/audit", {
      query: {
        actor: query.actor,
        action: query.action,
        documentId: query.documentId,
        occurredAfter: query.occurredAfter,
        occurredBefore: query.occurredBefore,
        pageSize: query.pageSize,
        cursor: query.cursor,
      },
    }),
};
