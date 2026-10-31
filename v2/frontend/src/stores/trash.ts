import { ref } from "vue";
import { defineStore } from "pinia";
import { trashApi } from "../api";
import { registerSensitiveStore } from "../session/lifecycle";
import type { TrashItem } from "../types/api";
import { ApiClientError, StaleSessionResponseError } from "../types/domain";

export const useTrashStore = defineStore("trash", () => {
  const items = ref<TrashItem[]>([]);
  const nextCursor = ref<string | null>(null);
  const loading = ref(false);
  const error = ref<ApiClientError | null>(null);
  const actionLoading = ref<string | null>(null);

  const load = async (append = false): Promise<void> => {
    if (append && !nextCursor.value) {
      return;
    }
    loading.value = true;
    error.value = null;
    try {
      const page = await trashApi.list(
        append ? (nextCursor.value ?? undefined) : undefined,
      );
      items.value = append ? [...items.value, ...page.items] : page.items;
      nextCursor.value = page.nextCursor ?? null;
    } catch (caught) {
      if (!(caught instanceof StaleSessionResponseError)) {
        error.value =
          caught instanceof ApiClientError
            ? caught
            : new ApiClientError(0, {
                code: "network_error",
                message: "无法读取回收站。",
              });
      }
    } finally {
      loading.value = false;
    }
  };

  const restore = async (
    item: TrashItem,
    replacementPath?: string,
  ): Promise<boolean> => {
    actionLoading.value = item.documentId;
    error.value = null;
    try {
      await trashApi.restore(
        item.documentId,
        item.revision,
        replacementPath ? { replacementPath } : {},
      );
      await load(false);
      return true;
    } catch (caught) {
      error.value =
        caught instanceof ApiClientError
          ? caught
          : new ApiClientError(0, {
              code: "network_error",
              message: "恢复文档失败。",
            });
      return false;
    } finally {
      actionLoading.value = null;
    }
  };

  const purge = async (item: TrashItem): Promise<boolean> => {
    actionLoading.value = item.documentId;
    error.value = null;
    try {
      await trashApi.purge(item.documentId, item.revision);
      items.value = items.value.filter(
        (candidate) => candidate.documentId !== item.documentId,
      );
      return true;
    } catch (caught) {
      error.value =
        caught instanceof ApiClientError
          ? caught
          : new ApiClientError(0, {
              code: "network_error",
              message: "永久删除失败。",
            });
      return false;
    } finally {
      actionLoading.value = null;
    }
  };

  const resetSensitive = (): void => {
    items.value = [];
    nextCursor.value = null;
    loading.value = false;
    error.value = null;
    actionLoading.value = null;
  };

  registerSensitiveStore({ resetSensitive });

  return {
    items,
    nextCursor,
    loading,
    error,
    actionLoading,
    load,
    restore,
    purge,
    resetSensitive,
  };
});
