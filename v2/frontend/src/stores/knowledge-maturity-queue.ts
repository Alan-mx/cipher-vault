import { ref } from "vue";
import { defineStore } from "pinia";
import { documentApi, knowledgeApi } from "../api";
import { registerSensitiveStore } from "../session/lifecycle";
import type {
  KnowledgeMaturityLevel,
  KnowledgeMaturityQueueBucket,
  KnowledgeMaturityQueueItem,
} from "../types/api";
import { ApiClientError, StaleSessionResponseError } from "../types/domain";

export const useKnowledgeMaturityQueueStore = defineStore(
  "knowledge-maturity-queue",
  () => {
    const bucket = ref<KnowledgeMaturityQueueBucket>("pending_organization");
    const items = ref<KnowledgeMaturityQueueItem[]>([]);
    const nextCursor = ref<string | null>(null);
    const loading = ref(false);
    const loadingMore = ref(false);
    const error = ref<ApiClientError | null>(null);
    const actionDocumentId = ref<string | null>(null);
    const actionError = ref<ApiClientError | null>(null);
    let generation = 0;
    let controller: AbortController | null = null;

    const load = async (
      targetBucket = bucket.value,
      append = false,
    ): Promise<boolean> => {
      if (append && !nextCursor.value) {
        return false;
      }
      controller?.abort();
      controller = new AbortController();
      const request = ++generation;
      bucket.value = targetBucket;
      if (append) {
        loadingMore.value = true;
      } else {
        loading.value = true;
        items.value = [];
        nextCursor.value = null;
      }
      error.value = null;
      try {
        const page = await knowledgeApi.maturityQueue(
          {
            bucket: targetBucket,
            pageSize: 30,
            cursor: append ? (nextCursor.value ?? undefined) : undefined,
          },
          controller.signal,
        );
        if (request !== generation) {
          return false;
        }
        items.value = append ? [...items.value, ...page.items] : page.items;
        nextCursor.value = page.nextCursor;
        return true;
      } catch (caught) {
        if (
          request !== generation ||
          caught instanceof StaleSessionResponseError ||
          (caught instanceof DOMException && caught.name === "AbortError")
        ) {
          return false;
        }
        error.value = toApiError(caught, "无法读取知识成熟度整理队列。");
        return false;
      } finally {
        if (request === generation) {
          loading.value = false;
          loadingMore.value = false;
          controller = null;
        }
      }
    };

    const setBucket = async (
      targetBucket: KnowledgeMaturityQueueBucket,
    ): Promise<void> => {
      if (bucket.value === targetBucket && !error.value) {
        return;
      }
      actionError.value = null;
      await load(targetBucket);
    };

    const refresh = async (): Promise<boolean> => load(bucket.value);

    const advance = async (
      item: KnowledgeMaturityQueueItem,
    ): Promise<boolean> => {
      if (!item.nextMaturity || item.format !== "md") {
        return false;
      }
      return setLevel(item, item.nextMaturity);
    };

    const setLevel = async (
      item: KnowledgeMaturityQueueItem,
      level: KnowledgeMaturityLevel,
    ): Promise<boolean> => {
      if (item.format !== "md") {
        return false;
      }
      if (
        item.maturityValueType === "string" &&
        normalizeStandard(item.maturityValue) === normalizeStandard(level)
      ) {
        return true;
      }
      return patchMaturity(item, level);
    };

    const clear = async (
      item: KnowledgeMaturityQueueItem,
    ): Promise<boolean> => {
      if (item.format !== "md" || item.maturityValueType === "missing") {
        return true;
      }
      return patchMaturity(item, null);
    };

    const patchMaturity = async (
      item: KnowledgeMaturityQueueItem,
      level: KnowledgeMaturityLevel | null,
    ): Promise<boolean> => {
      actionDocumentId.value = item.documentId;
      actionError.value = null;
      try {
        await documentApi.patch(item.documentId, item.revision, {
          targetKind: "front_matter",
          frontMatterKey: "maturity",
          operation: level === null ? "delete" : "set",
          value: level,
          dryRun: false,
        });
        await load(bucket.value);
        return true;
      } catch (caught) {
        const error = toApiError(caught, "更新知识成熟度失败。");
        actionError.value =
          error.code === "revision_conflict"
            ? new ApiClientError(409, {
                code: error.code,
                message: "文档版本已更新，请刷新队列后重新操作。",
                details: error.details,
                correlationId: error.correlationId,
              })
            : error;
        return false;
      } finally {
        actionDocumentId.value = null;
      }
    };

    const resetSensitive = (): void => {
      generation++;
      controller?.abort();
      controller = null;
      bucket.value = "pending_organization";
      items.value = [];
      nextCursor.value = null;
      loading.value = false;
      loadingMore.value = false;
      error.value = null;
      actionDocumentId.value = null;
      actionError.value = null;
    };

    registerSensitiveStore({ resetSensitive });

    return {
      bucket,
      items,
      nextCursor,
      loading,
      loadingMore,
      error,
      actionDocumentId,
      actionError,
      load,
      setBucket,
      refresh,
      advance,
      setLevel,
      clear,
      resetSensitive,
    };
  },
);

const normalizeStandard = (value: string | null): string =>
  (value ?? "").normalize("NFC").trim().toLowerCase();

const toApiError = (
  caught: unknown,
  fallbackMessage: string,
): ApiClientError =>
  caught instanceof ApiClientError
    ? caught
    : new ApiClientError(0, {
        code: "network_error",
        message: fallbackMessage,
      });
