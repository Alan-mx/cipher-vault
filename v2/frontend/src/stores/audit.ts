import { ref } from "vue";
import { defineStore } from "pinia";
import { auditApi } from "../api";
import { registerSensitiveStore } from "../session/lifecycle";
import type { AuditItem, AuditQuery } from "../types/api";
import { ApiClientError, type ActorKind } from "../types/domain";

export interface AuditFilters {
  actor: ActorKind | null;
  action: string;
  documentId: string;
  occurredAfter: string | null;
  occurredBefore: string | null;
}

export const useAuditStore = defineStore("audit", () => {
  const filters = ref<AuditFilters>({
    actor: null,
    action: "",
    documentId: "",
    occurredAfter: null,
    occurredBefore: null,
  });
  const items = ref<AuditItem[]>([]);
  const nextCursor = ref<string | null>(null);
  const loading = ref(false);
  const error = ref<ApiClientError | null>(null);

  const buildQuery = (cursor?: string): AuditQuery => ({
    ...(filters.value.actor ? { actor: filters.value.actor } : {}),
    ...(filters.value.action.trim()
      ? { action: filters.value.action.trim() }
      : {}),
    ...(filters.value.documentId.trim()
      ? { documentId: filters.value.documentId.trim() }
      : {}),
    ...(filters.value.occurredAfter
      ? { occurredAfter: filters.value.occurredAfter }
      : {}),
    ...(filters.value.occurredBefore
      ? { occurredBefore: filters.value.occurredBefore }
      : {}),
    pageSize: 20,
    ...(cursor ? { cursor } : {}),
  });

  const load = async (append = false): Promise<void> => {
    if (append && !nextCursor.value) {
      return;
    }
    loading.value = true;
    error.value = null;
    try {
      const page = await auditApi.list(
        buildQuery(append ? (nextCursor.value ?? undefined) : undefined),
      );
      items.value = append ? [...items.value, ...page.items] : page.items;
      nextCursor.value = page.nextCursor ?? null;
    } catch (caught) {
      error.value =
        caught instanceof ApiClientError
          ? caught
          : new ApiClientError(0, {
              code: "network_error",
              message: "无法读取活动记录。",
            });
    } finally {
      loading.value = false;
    }
  };

  const setFilters = (update: Partial<AuditFilters>): void => {
    filters.value = { ...filters.value, ...update };
  };

  const resetSensitive = (): void => {
    filters.value = {
      actor: null,
      action: "",
      documentId: "",
      occurredAfter: null,
      occurredBefore: null,
    };
    items.value = [];
    nextCursor.value = null;
    loading.value = false;
    error.value = null;
  };

  registerSensitiveStore({ resetSensitive });

  return {
    filters,
    items,
    nextCursor,
    loading,
    error,
    load,
    setFilters,
    resetSensitive,
  };
});
