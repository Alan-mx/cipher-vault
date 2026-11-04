import { ref } from "vue";
import { defineStore } from "pinia";
import { relationSuggestionsApi } from "../api";
import { registerSensitiveStore } from "../session/lifecycle";
import type { DocumentRelationItem } from "../types/api";
import { ApiClientError, StaleSessionResponseError } from "../types/domain";

export const useDocumentRelationsStore = defineStore(
  "document-relations",
  () => {
    const documentId = ref<string | null>(null);
    const items = ref<DocumentRelationItem[]>([]);
    const truncated = ref(false);
    const loading = ref(false);
    const actionLoading = ref(false);
    const error = ref<ApiClientError | null>(null);
    let generation = 0;
    let controller: AbortController | null = null;

    const load = async (id: string, force = false): Promise<void> => {
      if (!force && documentId.value === id && !loading.value && !error.value) {
        return;
      }
      controller?.abort();
      controller = new AbortController();
      const request = ++generation;
      documentId.value = id;
      loading.value = true;
      error.value = null;
      try {
        const result = await relationSuggestionsApi.documentRelations(
          id,
          controller.signal,
        );
        if (request !== generation || documentId.value !== id) {
          return;
        }
        items.value = result.items;
        truncated.value = result.truncated;
      } catch (caught) {
        if (
          request !== generation ||
          caught instanceof StaleSessionResponseError ||
          (caught instanceof DOMException && caught.name === "AbortError")
        ) {
          return;
        }
        items.value = [];
        truncated.value = false;
        error.value =
          caught instanceof ApiClientError
            ? caught
            : new ApiClientError(0, {
                code: "network_error",
                message: "无法读取已确认关系。",
              });
      } finally {
        if (request === generation) {
          loading.value = false;
          controller = null;
        }
      }
    };

    const retract = async (relationId: string): Promise<boolean> => {
      actionLoading.value = true;
      error.value = null;
      try {
        await relationSuggestionsApi.retractRelation(
          relationId,
          crypto.randomUUID(),
        );
        if (documentId.value) {
          await load(documentId.value, true);
        }
        return true;
      } catch (caught) {
        error.value =
          caught instanceof ApiClientError
            ? caught
            : new ApiClientError(0, {
                code: "network_error",
                message: "撤销正式关系失败。",
              });
        return false;
      } finally {
        actionLoading.value = false;
      }
    };

    const resetSensitive = (): void => {
      generation++;
      controller?.abort();
      controller = null;
      documentId.value = null;
      items.value = [];
      truncated.value = false;
      loading.value = false;
      actionLoading.value = false;
      error.value = null;
    };

    registerSensitiveStore({ resetSensitive });

    return {
      documentId,
      items,
      truncated,
      loading,
      actionLoading,
      error,
      load,
      retract,
      resetSensitive,
    };
  },
);
