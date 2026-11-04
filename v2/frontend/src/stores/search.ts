import { computed, ref } from "vue";
import { defineStore } from "pinia";
import { knowledgeApi } from "../api";
import { registerSensitiveStore } from "../session/lifecycle";
import type { SearchHit, SearchPage, SearchQuery } from "../types/api";
import type {
  SearchPropertyFilter,
  SearchPropertyMatch,
  SearchPropertyOperator,
} from "../types/api";
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
  propertyMatch: SearchPropertyMatch;
  propertyFilters: SearchPropertyFilterDraft[];
}

export type SearchPropertyValueType = "string" | "number" | "boolean" | "null";

export interface SearchPropertyFilterDraft {
  id: string;
  field: string;
  operator: SearchPropertyOperator;
  valueType: SearchPropertyValueType;
  value: string;
  booleanValue: boolean;
  valuesText: string;
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
    propertyMatch: "all",
    propertyFilters: [],
  });
  const hits = ref<SearchHit[]>([]);
  const coverage = ref<SearchPage["coverage"] | null>(null);
  const nextCursor = ref<string | null>(null);
  const loading = ref(false);
  const error = ref<ApiClientError | null>(null);
  const canSearch = computed(
    () =>
      query.value.trim().length > 0 ||
      filters.value.tags.length > 0 ||
      filters.value.format !== null ||
      filters.value.updatedAfter !== null ||
      filters.value.updatedBefore !== null ||
      filters.value.hasBacklinks ||
      filters.value.orphanOnly ||
      toApiPropertyFilters(filters.value.propertyFilters).length > 0,
  );
  let requestGeneration = 0;
  let controller: AbortController | null = null;

  const buildQuery = (cursor?: string): SearchQuery => {
    const propertyFilters = toApiPropertyFilters(filters.value.propertyFilters);
    return {
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
      ...(propertyFilters.length
        ? {
            propertyMatch: filters.value.propertyMatch,
            propertyFilters,
          }
        : {}),
      pageSize: 30,
      ...(cursor ? { cursor } : {}),
    };
  };

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
    if (!canSearch.value) {
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

  const addPropertyFilter = (): void => {
    if (filters.value.propertyFilters.length >= 16) {
      return;
    }
    filters.value.propertyFilters.push({
      id: crypto.randomUUID(),
      field: "",
      operator: "eq",
      valueType: "string",
      value: "",
      booleanValue: true,
      valuesText: "",
    });
  };

  const updatePropertyFilter = (
    id: string,
    update: Partial<SearchPropertyFilterDraft>,
  ): void => {
    const index = filters.value.propertyFilters.findIndex(
      (item) => item.id === id,
    );
    if (index < 0) {
      return;
    }
    filters.value.propertyFilters[index] = {
      ...filters.value.propertyFilters[index]!,
      ...update,
    };
  };

  const removePropertyFilter = (id: string): void => {
    filters.value.propertyFilters = filters.value.propertyFilters.filter(
      (item) => item.id !== id,
    );
  };

  const setPropertyMatch = (value: SearchPropertyMatch): void => {
    filters.value.propertyMatch = value;
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
      propertyMatch: "all",
      propertyFilters: [],
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
    addPropertyFilter,
    updatePropertyFilter,
    removePropertyFilter,
    setPropertyMatch,
    resetSensitive,
  };
});

const toApiPropertyFilters = (
  drafts: SearchPropertyFilterDraft[],
): SearchPropertyFilter[] =>
  drafts.flatMap((draft) => {
    const field = draft.field.trim();
    if (!field) {
      return [];
    }
    const value = propertyValue(draft);
    if (value === undefined) {
      return [];
    }
    return [{ field, operator: draft.operator, value }];
  });

const propertyValue = (
  draft: SearchPropertyFilterDraft,
): unknown | undefined => {
  if (draft.operator === "exists") {
    return draft.booleanValue;
  }
  if (draft.operator === "in") {
    if (draft.valueType === "null") {
      return [null];
    }
    if (draft.valueType === "boolean") {
      return [draft.booleanValue];
    }
    const values = draft.valuesText
      .split(",")
      .map((value) => value.trim())
      .filter(Boolean)
      .map((value) => parsePropertyValue(draft.valueType, value))
      .filter((value) => value !== undefined);
    return values.length ? values : undefined;
  }
  if (draft.valueType === "null") {
    return null;
  }
  if (draft.valueType === "boolean") {
    return draft.booleanValue;
  }
  return parsePropertyValue(draft.valueType, draft.value.trim());
};

const parsePropertyValue = (
  valueType: SearchPropertyValueType,
  value: string,
): unknown | undefined => {
  if (valueType === "string") {
    return value || undefined;
  }
  if (valueType === "number") {
    const number = Number(value);
    return value !== "" && Number.isFinite(number) ? number : undefined;
  }
  if (valueType === "boolean") {
    return value === "true";
  }
  return null;
};
