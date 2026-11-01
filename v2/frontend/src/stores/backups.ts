import { ref } from "vue";
import { defineStore } from "pinia";
import { backupApi } from "../api";
import { registerSensitiveStore } from "../session/lifecycle";
import type { BackupListItem, GitBackupStatus } from "../types/api";
import { ApiClientError } from "../types/domain";

export const useBackupStore = defineStore("backups", () => {
  const items = ref<BackupListItem[]>([]);
  const nextCursor = ref<string | null>(null);
  const loading = ref(false);
  const loadingMore = ref(false);
  const creating = ref(false);
  const error = ref<ApiClientError | null>(null);
  const gitStatus = ref<GitBackupStatus | null>(null);
  const gitStatusLoading = ref(false);
  const gitStatusError = ref<ApiClientError | null>(null);

  const readError = (caught: unknown, fallback: string): ApiClientError =>
    caught instanceof ApiClientError
      ? caught
      : new ApiClientError(0, {
          code: "network_error",
          message: fallback,
        });

  const load = async (): Promise<void> => {
    loading.value = true;
    error.value = null;
    try {
      const page = await backupApi.list({ pageSize: 50 });
      items.value = page.items;
      nextCursor.value = page.nextCursor ?? null;
    } catch (caught) {
      error.value = readError(caught, "无法读取备份列表。");
    } finally {
      loading.value = false;
    }
  };

  const loadMore = async (): Promise<void> => {
    if (!nextCursor.value || loadingMore.value) {
      return;
    }
    loadingMore.value = true;
    error.value = null;
    try {
      const page = await backupApi.list({
        pageSize: 50,
        cursor: nextCursor.value,
      });
      items.value = [...items.value, ...page.items];
      nextCursor.value = page.nextCursor ?? null;
    } catch (caught) {
      error.value = readError(caught, "无法加载更多备份。");
    } finally {
      loadingMore.value = false;
    }
  };

  const create = async (): Promise<BackupListItem | null> => {
    if (creating.value) {
      return null;
    }
    creating.value = true;
    error.value = null;
    try {
      const result = await backupApi.create();
      await load();
      return result.backup;
    } catch (caught) {
      error.value = readError(caught, "创建加密备份失败。");
      return null;
    } finally {
      creating.value = false;
    }
  };

  const downloadUrl = (fileName: string): string =>
    backupApi.downloadUrl(fileName);

  const loadGitStatus = async (): Promise<void> => {
    gitStatusLoading.value = true;
    gitStatusError.value = null;
    try {
      gitStatus.value = await backupApi.getGitStatus();
    } catch (caught) {
      gitStatusError.value = readError(caught, "无法读取自动备份状态。");
    } finally {
      gitStatusLoading.value = false;
    }
  };

  const resetSensitive = (): void => {
    items.value = [];
    nextCursor.value = null;
    loading.value = false;
    loadingMore.value = false;
    creating.value = false;
    error.value = null;
    gitStatus.value = null;
    gitStatusLoading.value = false;
    gitStatusError.value = null;
  };

  registerSensitiveStore({ resetSensitive });

  return {
    items,
    nextCursor,
    loading,
    loadingMore,
    creating,
    error,
    gitStatus,
    gitStatusLoading,
    gitStatusError,
    load,
    loadMore,
    create,
    downloadUrl,
    loadGitStatus,
    resetSensitive,
  };
});
