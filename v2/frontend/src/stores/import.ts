import { ref } from "vue";
import { defineStore } from "pinia";
import { importApi } from "../api";
import { registerSensitiveStore } from "../session/lifecycle";
import type {
  ImportCommitResult,
  ImportPreviewItem,
  ImportPreviewResult,
} from "../types/api";
import { ApiClientError } from "../types/domain";

export const useImportStore = defineStore("import", () => {
  const sourceRoot = ref("");
  const preview = ref<ImportPreviewResult | null>(null);
  const items = ref<ImportPreviewItem[]>([]);
  const commitResult = ref<ImportCommitResult | null>(null);
  const loading = ref(false);
  const committing = ref(false);
  const error = ref<ApiClientError | null>(null);

  const runPreview = async (): Promise<void> => {
    loading.value = true;
    error.value = null;
    commitResult.value = null;
    try {
      const result = await importApi.preview({
        sourceRoot: sourceRoot.value.trim(),
      });
      preview.value = result;
      items.value = result.items;
    } catch (caught) {
      error.value =
        caught instanceof ApiClientError
          ? caught
          : new ApiClientError(0, {
              code: "network_error",
              message: "导入预览失败。",
            });
    } finally {
      loading.value = false;
    }
  };

  const commit = async (): Promise<void> => {
    const token = preview.value?.token;
    if (!token) {
      return;
    }
    committing.value = true;
    error.value = null;
    try {
      commitResult.value = await importApi.commit(token);
      preview.value = null;
    } catch (caught) {
      error.value =
        caught instanceof ApiClientError
          ? caught
          : new ApiClientError(0, {
              code: "network_error",
              message: "导入提交失败。",
            });
    } finally {
      committing.value = false;
    }
  };

  const resetSensitive = (): void => {
    sourceRoot.value = "";
    preview.value = null;
    items.value = [];
    commitResult.value = null;
    loading.value = false;
    committing.value = false;
    error.value = null;
  };

  registerSensitiveStore({ resetSensitive });

  return {
    sourceRoot,
    preview,
    items,
    commitResult,
    loading,
    committing,
    error,
    runPreview,
    commit,
    resetSensitive,
  };
});
