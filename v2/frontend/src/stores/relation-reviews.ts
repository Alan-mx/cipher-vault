import { computed, ref } from "vue";
import { defineStore } from "pinia";
import { relationSuggestionsApi } from "../api";
import { registerSensitiveStore } from "../session/lifecycle";
import type {
  RelationSuggestionDetail,
  RelationSuggestionListItem,
  RelationSuggestionStatus,
  RelationSuggestionSummary,
  RelationType,
} from "../types/api";
import { ApiClientError, StaleSessionResponseError } from "../types/domain";

export interface RelationReviewFilters {
  status: RelationSuggestionStatus;
  relationType: RelationType | null;
}

export const useRelationReviewsStore = defineStore("relation-reviews", () => {
  const filters = ref<RelationReviewFilters>({
    status: "pending_review",
    relationType: null,
  });
  const summary = ref<RelationSuggestionSummary | null>(null);
  const items = ref<RelationSuggestionListItem[]>([]);
  const nextCursor = ref<string | null>(null);
  const selected = ref<RelationSuggestionDetail | null>(null);
  const summaryLoading = ref(false);
  const loading = ref(false);
  const detailLoading = ref(false);
  const actionLoading = ref(false);
  const error = ref<ApiClientError | null>(null);
  let requestGeneration = 0;
  let detailGeneration = 0;
  let listController: AbortController | null = null;
  let detailController: AbortController | null = null;

  const hasMore = computed(() => nextCursor.value !== null);

  const loadSummary = async (): Promise<void> => {
    summaryLoading.value = true;
    try {
      summary.value = await relationSuggestionsApi.summary();
    } catch (caught) {
      if (!(caught instanceof StaleSessionResponseError)) {
        error.value = toApiError(caught, "无法读取关系建议统计。");
      }
    } finally {
      summaryLoading.value = false;
    }
  };

  const load = async (append = false): Promise<void> => {
    if (append && !nextCursor.value) {
      return;
    }
    listController?.abort();
    listController = new AbortController();
    const generation = ++requestGeneration;
    loading.value = true;
    error.value = null;
    try {
      const query = {
        status: filters.value.status,
        pageSize: 30,
      } as {
        status: RelationSuggestionStatus;
        pageSize: number;
        relationType?: RelationType;
        cursor?: string;
      };
      if (filters.value.relationType) {
        query.relationType = filters.value.relationType;
      }
      if (append && nextCursor.value) {
        query.cursor = nextCursor.value;
      }
      const page = await relationSuggestionsApi.list(
        query,
        listController.signal,
      );
      if (generation !== requestGeneration) {
        return;
      }
      items.value = append ? [...items.value, ...page.items] : page.items;
      nextCursor.value = page.nextCursor ?? null;
    } catch (caught) {
      if (
        generation !== requestGeneration ||
        caught instanceof StaleSessionResponseError ||
        (caught instanceof DOMException && caught.name === "AbortError")
      ) {
        return;
      }
      error.value = toApiError(caught, "无法读取关系建议。");
    } finally {
      if (generation === requestGeneration) {
        loading.value = false;
        listController = null;
      }
    }
  };

  const select = async (suggestionId: string): Promise<void> => {
    detailController?.abort();
    detailController = new AbortController();
    const generation = ++detailGeneration;
    detailLoading.value = true;
    error.value = null;
    try {
      const detail = await relationSuggestionsApi.get(
        suggestionId,
        detailController.signal,
      );
      if (generation === detailGeneration) {
        selected.value = detail;
      }
    } catch (caught) {
      if (generation !== detailGeneration) {
        return;
      }
      if (
        caught instanceof StaleSessionResponseError ||
        (caught instanceof DOMException && caught.name === "AbortError")
      ) {
        return;
      }
      selected.value = null;
      error.value = toApiError(caught, "无法读取关系建议详情。");
    } finally {
      if (generation === detailGeneration) {
        detailLoading.value = false;
        detailController = null;
      }
    }
  };

  const setStatus = async (status: RelationSuggestionStatus): Promise<void> => {
    filters.value = { ...filters.value, status };
    selected.value = null;
    nextCursor.value = null;
    await load();
  };

  const setRelationType = async (
    relationType: RelationType | null,
  ): Promise<void> => {
    filters.value = { ...filters.value, relationType };
    selected.value = null;
    nextCursor.value = null;
    await load();
  };

  const refresh = async (): Promise<void> => {
    await Promise.all([loadSummary(), load()]);
  };

  const applySelected = async (): Promise<boolean> => {
    const detail = selected.value;
    if (!detail || detail.metadata.status !== "pending_review") {
      return false;
    }
    actionLoading.value = true;
    error.value = null;
    try {
      await relationSuggestionsApi.apply(
        detail.metadata.suggestionId,
        detail.metadata.suggestionRevision,
        crypto.randomUUID(),
      );
      selected.value = null;
      await refresh();
      return true;
    } catch (caught) {
      error.value = toApiError(caught, "应用关系建议失败。");
      return false;
    } finally {
      actionLoading.value = false;
    }
  };

  const rejectSelected = async (): Promise<boolean> => {
    const detail = selected.value;
    if (!detail || detail.metadata.status !== "pending_review") {
      return false;
    }
    actionLoading.value = true;
    error.value = null;
    try {
      await relationSuggestionsApi.reject(
        detail.metadata.suggestionId,
        detail.metadata.suggestionRevision,
        crypto.randomUUID(),
      );
      selected.value = null;
      await refresh();
      return true;
    } catch (caught) {
      error.value = toApiError(caught, "拒绝关系建议失败。");
      return false;
    } finally {
      actionLoading.value = false;
    }
  };

  const resetSensitive = (): void => {
    requestGeneration++;
    detailGeneration++;
    listController?.abort();
    detailController?.abort();
    listController = null;
    detailController = null;
    filters.value = {
      status: "pending_review",
      relationType: null,
    };
    summary.value = null;
    items.value = [];
    nextCursor.value = null;
    selected.value = null;
    summaryLoading.value = false;
    loading.value = false;
    detailLoading.value = false;
    actionLoading.value = false;
    error.value = null;
  };

  registerSensitiveStore({ resetSensitive });

  return {
    filters,
    summary,
    items,
    nextCursor,
    selected,
    summaryLoading,
    loading,
    detailLoading,
    actionLoading,
    error,
    hasMore,
    loadSummary,
    load,
    select,
    setStatus,
    setRelationType,
    refresh,
    applySelected,
    rejectSelected,
    resetSensitive,
  };
});

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
