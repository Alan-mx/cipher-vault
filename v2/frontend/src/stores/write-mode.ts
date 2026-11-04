import { ref } from "vue";
import { defineStore } from "pinia";
import { writeModeApi } from "../api/write-mode";
import type { McpWriteMode, McpWriteModeStatus } from "../types/api";
import { ApiClientError } from "../types/domain";

export const useWriteModeStore = defineStore("write-mode", () => {
  const status = ref<McpWriteModeStatus | null>(null);
  const loading = ref(false);
  const changing = ref(false);
  const error = ref<ApiClientError | null>(null);

  const load = async (): Promise<void> => {
    loading.value = true;
    error.value = null;
    try {
      status.value = await writeModeApi.get();
    } catch (cause) {
      error.value =
        cause instanceof ApiClientError ? cause : new ApiClientError(0, {});
    } finally {
      loading.value = false;
    }
  };

  const setMode = async (mode: McpWriteMode): Promise<boolean> => {
    changing.value = true;
    error.value = null;
    try {
      status.value = await writeModeApi.set(mode, crypto.randomUUID());
      return true;
    } catch (cause) {
      error.value =
        cause instanceof ApiClientError ? cause : new ApiClientError(0, {});
      return false;
    } finally {
      changing.value = false;
    }
  };

  const resetSensitive = (): void => {
    status.value = null;
    loading.value = false;
    changing.value = false;
    error.value = null;
  };

  return {
    status,
    loading,
    changing,
    error,
    load,
    setMode,
    resetSensitive,
  };
});
