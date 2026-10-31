import { computed, ref } from "vue";
import { defineStore } from "pinia";
import { knowledgeApi } from "../api";
import { registerSensitiveStore } from "../session/lifecycle";
import type { SearchHit, SearchPage, SearchQuery } from "../types/api";
import {
  ApiClientError,
  StaleSessionResponseError,
  type VaultFormat,
} from "../types/domain";

export interface SearchFilters {
  tags: string[];
  format: VaultFormat | null;
  updatedAfter: string | null;
  updatedBefore: string | null;
  hasBacklinks: boolean;
  orphanOnly: boolean;
}

export const useSearchStore = defineStore("search", () => {
  const query = ref("");
  const filters = ref<SearchFilters>({
    tags: [],
    format: null,
    updatedAfter: null,
    updatedBefore: null,
    hasBacklinks: false,
    orphanOnly: false,
  });
  const hits = ref<SearchHit[]>([]);
  const coverage = ref<SearchPage["coverage"] | null>(null);
  const nextCursor = ref<string | null>(null);
  const loading = ref(false);
  const error = ref<ApiClientError | null>(null);
  const canSearch = computed(() => query.value.trim().length > 0);
  let requestGeneration = 0;
  let controller: AbortController | null = null;

  const buildQuery = (cursor?: string): SearchQuery => ({
    query: query.value.trim(),
    ...(filters.value.tags.length ? { tags: filters.value.tags } : {}),
    ...(filters.value.format ? { format: filters.value.format } : {}),
    ...(filters.value.updatedAfter
      ? { updatedAfter: filters.value.updatedAfter }
      : {}),
    ...(filters.value.updatedBefore
      ? { updatedBefore: filters.value.updatedBefore }
      : {}),
    ...(filters.value.hasBacklinks ? { hasBacklinks: true } : {}),
    orphanOnly: filters.value.orphanOnly,
    pageSize: 30,
    ...(cursor ? { cursor } : {}),
  });

  const search = async (append = false): Promise<void> => {
    if (!canSearch.value || (append && !nextCursor.value)) {
      return;
    }

    controller?.abort();
    const generation = ++requestGeneration;
    controller = new AbortController();
    loading.value = true;
    error.value = null;
    try {
      const page = await knowledgeApi.search(
        buildQuery(append ? (nextCursor.value ?? undefined) : undefined),
        controller.signal,
      );
      if (generation !== requestGeneration) {
        return;
      }
      hits.value = append ? [...hits.value, ...page.hits] : page.hits;
      coverage.value = page.coverage;
      nextCursor.value = page.nextCursor ?? null;
    } catch (caught) {
      if (
        generation !== requestGeneration ||
        caught instanceof StaleSessionResponseError
      ) {
        return;
      }
      if (caught instanceof DOMException && caught.name === "AbortError") {
        return;
      }
      error.value =
        caught instanceof ApiClientError
          ? caught
          : new ApiClientError(0, {
              code: "network_error",
              message: "搜索请求失败。",
            });
    } finally {
      if (generation === requestGeneration) {
        loading.value = false;
      }
    }
  };

  const setQuery = (value: string): void => {
    query.value = value;
    if (!value.trim()) {
      requestGeneration++;
      controller?.abort();
      hits.value = [];
      coverage.value = null;
      nextCursor.value = null;
      error.value = null;
      loading.value = false;
    }
  };

  const setFilters = (update: Partial<SearchFilters>): void => {
    filters.value = { ...filters.value, ...update };
  };

  const resetSensitive = (): void => {
    requestGeneration++;
    controller?.abort();
    controller = null;
    query.value = "";
    filters.value = {
      tags: [],
      format: null,
      updatedAfter: null,
      updatedBefore: null,
      hasBacklinks: false,
      orphanOnly: false,
    };
    hits.value = [];
    coverage.value = null;
    nextCursor.value = null;
    error.value = null;
    loading.value = false;
  };

  registerSensitiveStore({ resetSensitive });

  return {
    query,
    filters,
    hits,
    coverage,
    nextCursor,
    loading,
    error,
    canSearch,
    search,
    setQuery,
    setFilters,
    resetSensitive,
  };
});
