import { computed, ref } from "vue";
import { defineStore } from "pinia";
import { catalogApi } from "../api";
import { registerSensitiveStore } from "../session/lifecycle";
import type { CatalogItem, CatalogQuery, TagItem } from "../types/api";
import {
  ApiClientError,
  StaleSessionResponseError,
  type VaultFormat,
} from "../types/domain";
import { collectFolders } from "../utils/paths";

export interface CatalogFilters {
  folder: string | null;
  format: VaultFormat | null;
  tags: string[];
  orphanOnly: boolean;
}

export const useCatalogStore = defineStore("catalog", () => {
  const filters = ref<CatalogFilters>({
    folder: null,
    format: null,
    tags: [],
    orphanOnly: false,
  });
  const items = ref<CatalogItem[]>([]);
  const tags = ref<TagItem[]>([]);
  const tagCursor = ref<string | null>(null);
  const nextCursor = ref<string | null>(null);
  const loading = ref(false);
  const loadingTags = ref(false);
  const error = ref<ApiClientError | null>(null);
  let requestGeneration = 0;

  const folders = computed(() =>
    collectFolders(items.value.map((item) => item.virtualPath)),
  );

  const query = (cursor?: string): CatalogQuery => ({
    ...(filters.value.folder ? { folder: filters.value.folder } : {}),
    ...(filters.value.format ? { format: filters.value.format } : {}),
    ...(filters.value.tags.length ? { tags: filters.value.tags } : {}),
    orphanOnly: filters.value.orphanOnly,
    pageSize: 30,
    ...(cursor ? { cursor } : {}),
  });

  const load = async (append: boolean): Promise<void> => {
    if (append && !nextCursor.value) {
      return;
    }

    const generation = ++requestGeneration;
    loading.value = true;
    error.value = null;
    try {
      const page = await catalogApi.list(
        query(append ? (nextCursor.value ?? undefined) : undefined),
      );
      if (generation !== requestGeneration) {
        return;
      }
      items.value = append ? [...items.value, ...page.items] : page.items;
      nextCursor.value = page.nextCursor ?? null;
    } catch (caught) {
      if (caught instanceof StaleSessionResponseError) {
        return;
      }
      if (generation === requestGeneration) {
        error.value =
          caught instanceof ApiClientError
            ? caught
            : new ApiClientError(0, {
                code: "network_error",
                message: "无法加载文档目录。",
              });
      }
    } finally {
      if (generation === requestGeneration) {
        loading.value = false;
      }
    }
  };

  const setFilters = async (update: Partial<CatalogFilters>): Promise<void> => {
    filters.value = {
      ...filters.value,
      ...update,
      tags: update.tags ?? filters.value.tags,
    };
    nextCursor.value = null;
    await load(false);
  };

  const toggleTag = async (name: string): Promise<void> => {
    const selected = filters.value.tags.includes(name)
      ? filters.value.tags.filter((tag) => tag !== name)
      : [...filters.value.tags, name];
    await setFilters({ tags: selected });
  };

  const loadTags = async (append = false): Promise<void> => {
    if (append && !tagCursor.value) {
      return;
    }
    loadingTags.value = true;
    try {
      const page = await catalogApi.tags(
        200,
        append ? (tagCursor.value ?? undefined) : undefined,
      );
      tags.value = append ? [...tags.value, ...page.items] : page.items;
      tagCursor.value = page.nextCursor ?? null;
    } catch (caught) {
      if (!(caught instanceof StaleSessionResponseError)) {
        error.value =
          caught instanceof ApiClientError
            ? caught
            : new ApiClientError(0, {
                code: "network_error",
                message: "无法加载标签。",
              });
      }
    } finally {
      loadingTags.value = false;
    }
  };

  const refreshAfterWrite = (documentId: string): void => {
    const index = items.value.findIndex(
      (item) => item.documentId === documentId,
    );
    if (index >= 0) {
      items.value.splice(index, 1);
    }
  };

  const resetSensitive = (): void => {
    requestGeneration++;
    filters.value = {
      folder: null,
      format: null,
      tags: [],
      orphanOnly: false,
    };
    items.value = [];
    tags.value = [];
    tagCursor.value = null;
    nextCursor.value = null;
    error.value = null;
    loading.value = false;
    loadingTags.value = false;
  };

  registerSensitiveStore({ resetSensitive });

  return {
    filters,
    items,
    tags,
    tagCursor,
    nextCursor,
    loading,
    loadingTags,
    error,
    folders,
    load,
    loadTags,
    setFilters,
    toggleTag,
    refreshAfterWrite,
    resetSensitive,
  };
});
