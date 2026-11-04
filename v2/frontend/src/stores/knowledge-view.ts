import { ref } from "vue";
import { defineStore } from "pinia";
import { knowledgeApi } from "../api";
import { registerSensitiveStore } from "../session/lifecycle";
import type {
  KnowledgeFacetItem,
  KnowledgeViewField,
  KnowledgeViewItem,
} from "../types/api";
import { ApiClientError, StaleSessionResponseError } from "../types/domain";

export const useKnowledgeViewStore = defineStore("knowledge-view", () => {
  const facets = ref<KnowledgeFacetItem[]>([]);
  const facetsLoading = ref(false);
  const facetsError = ref<ApiClientError | null>(null);
  const facetsTruncated = ref(false);

  const documents = ref<KnowledgeViewItem[]>([]);
  const documentsNextCursor = ref<string | null>(null);
  const documentsLoading = ref(false);
  const documentsError = ref<ApiClientError | null>(null);

  let facetsRequestGeneration = 0;
  let documentsRequestGeneration = 0;
  let facetsController: AbortController | null = null;
  let documentsController: AbortController | null = null;

  const loadFacets = async (
    field: KnowledgeViewField,
    activeOnly: boolean,
  ): Promise<boolean> => {
    facetsController?.abort();
    const generation = ++facetsRequestGeneration;
    facetsController = new AbortController();
    facetsLoading.value = true;
    facetsError.value = null;
    facets.value = [];
    facetsTruncated.value = false;
    try {
      const page = await knowledgeApi.viewFacets(
        field,
        activeOnly,
        facetsController.signal,
      );
      if (generation !== facetsRequestGeneration) {
        return false;
      }
      facets.value = page.items;
      facetsTruncated.value = page.truncated;
      return true;
    } catch (caught) {
      if (
        generation !== facetsRequestGeneration ||
        caught instanceof StaleSessionResponseError ||
        (caught instanceof DOMException && caught.name === "AbortError")
      ) {
        return false;
      }
      facetsError.value = toKnowledgeViewError(
        caught,
        "无法读取知识视图分类。",
      );
      return false;
    } finally {
      if (generation === facetsRequestGeneration) {
        facetsLoading.value = false;
      }
    }
  };

  const loadDocuments = async (
    field: KnowledgeViewField,
    selection: {
      facetKey?: string | undefined;
      unset?: boolean | undefined;
    },
    activeOnly: boolean,
    append = false,
  ): Promise<boolean> => {
    if (!selection.unset && !selection.facetKey) {
      clearDocuments();
      return false;
    }
    if (append && !documentsNextCursor.value) {
      return false;
    }

    documentsController?.abort();
    const generation = ++documentsRequestGeneration;
    documentsController = new AbortController();
    documentsLoading.value = true;
    documentsError.value = null;
    if (!append) {
      documents.value = [];
      documentsNextCursor.value = null;
    }
    try {
      const page = await knowledgeApi.viewDocuments(
        field,
        {
          facetKey: selection.facetKey,
          unset: selection.unset,
          activeOnly,
          pageSize: 30,
          cursor: append ? (documentsNextCursor.value ?? undefined) : undefined,
        },
        documentsController.signal,
      );
      if (generation !== documentsRequestGeneration) {
        return false;
      }
      documents.value = append
        ? [...documents.value, ...page.items]
        : page.items;
      documentsNextCursor.value = page.nextCursor;
      return true;
    } catch (caught) {
      if (
        generation !== documentsRequestGeneration ||
        caught instanceof StaleSessionResponseError ||
        (caught instanceof DOMException && caught.name === "AbortError")
      ) {
        return false;
      }
      documentsError.value = toKnowledgeViewError(caught, "无法读取分类文档。");
      return false;
    } finally {
      if (generation === documentsRequestGeneration) {
        documentsLoading.value = false;
      }
    }
  };

  const clearDocuments = (): void => {
    documentsRequestGeneration++;
    documentsController?.abort();
    documentsController = null;
    documents.value = [];
    documentsNextCursor.value = null;
    documentsError.value = null;
    documentsLoading.value = false;
  };

  const resetSensitive = (): void => {
    facetsRequestGeneration++;
    documentsRequestGeneration++;
    facetsController?.abort();
    documentsController?.abort();
    facetsController = null;
    documentsController = null;
    facets.value = [];
    facetsLoading.value = false;
    facetsError.value = null;
    facetsTruncated.value = false;
    documents.value = [];
    documentsNextCursor.value = null;
    documentsLoading.value = false;
    documentsError.value = null;
  };

  registerSensitiveStore({ resetSensitive });

  return {
    facets,
    facetsLoading,
    facetsError,
    facetsTruncated,
    documents,
    documentsNextCursor,
    documentsLoading,
    documentsError,
    loadFacets,
    loadDocuments,
    clearDocuments,
    resetSensitive,
  };
});

const toKnowledgeViewError = (
  caught: unknown,
  fallbackMessage: string,
): ApiClientError =>
  caught instanceof ApiClientError
    ? caught
    : new ApiClientError(0, {
        code: "network_error",
        message: fallbackMessage,
      });
