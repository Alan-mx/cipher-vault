import { ref } from "vue";
import { defineStore } from "pinia";
import { draftsApi } from "../api/drafts";
import type {
  DocumentDraftApplyResult,
  DocumentDraftDetail,
  DocumentDraftItem,
  DocumentDraftMutationResult,
  DocumentDraftStatus,
} from "../types/api";
import { ApiClientError } from "../types/domain";

export const useDocumentDraftsStore = defineStore("document-drafts", () => {
  const items = ref<DocumentDraftItem[]>([]);
  const nextCursor = ref<string | null>(null);
  const selected = ref<DocumentDraftDetail | null>(null);
  const loading = ref(false);
  const actionLoading = ref(false);
  const error = ref<ApiClientError | null>(null);
  const statusFilter = ref<DocumentDraftStatus>("pending_review");

  const load = async (
    append = false,
    status = statusFilter.value,
  ): Promise<void> => {
    loading.value = true;
    error.value = null;
    statusFilter.value = status;
    try {
      const query = {
        status,
        pageSize: 50,
        ...(append && nextCursor.value ? { cursor: nextCursor.value } : {}),
      };
      const page = await draftsApi.list(query);
      items.value = append ? [...items.value, ...page.items] : page.items;
      nextCursor.value = page.nextCursor ?? null;
    } catch (cause) {
      error.value = toError(cause);
    } finally {
      loading.value = false;
    }
  };

  const select = async (draftId: string): Promise<void> => {
    loading.value = true;
    error.value = null;
    try {
      selected.value = await draftsApi.get(draftId);
    } catch (cause) {
      error.value = toError(cause);
    } finally {
      loading.value = false;
    }
  };

  const save = async (payload: Record<string, unknown>): Promise<boolean> => {
    if (!selected.value) {
      return false;
    }
    return runMutation(() =>
      draftsApi.update(
        selected.value!.metadata.draftId,
        {
          expectedDraftRevision: selected.value!.metadata.draftRevision,
          payload,
        },
        crypto.randomUUID(),
      ),
    );
  };

  const rebase = async (observedDocumentRevision: number): Promise<boolean> => {
    if (!selected.value) {
      return false;
    }
    return runMutation(() =>
      draftsApi.rebase(
        selected.value!.metadata.draftId,
        selected.value!.metadata.draftRevision,
        observedDocumentRevision,
        crypto.randomUUID(),
      ),
    );
  };

  const submitReview = (): Promise<boolean> =>
    runRevisionAction("submit-review");

  const reject = (): Promise<boolean> => runRevisionAction("reject");

  const discard = (): Promise<boolean> => runRevisionAction("discard");

  const apply = async (): Promise<DocumentDraftApplyResult | null> => {
    if (!selected.value) {
      return null;
    }
    actionLoading.value = true;
    error.value = null;
    try {
      const result = await draftsApi.apply(
        selected.value.metadata.draftId,
        selected.value.metadata.draftRevision,
        crypto.randomUUID(),
      );
      await refreshAfterMutation(result);
      return result;
    } catch (cause) {
      error.value = toError(cause);
      return null;
    } finally {
      actionLoading.value = false;
    }
  };

  const runRevisionAction = async (
    action: "submit-review" | "reject" | "discard",
  ): Promise<boolean> => {
    if (!selected.value) {
      return false;
    }
    const draftId = selected.value.metadata.draftId;
    const revision = selected.value.metadata.draftRevision;
    const key = crypto.randomUUID();
    return runMutation(() =>
      action === "submit-review"
        ? draftsApi.submitReview(draftId, revision, key)
        : action === "reject"
          ? draftsApi.reject(draftId, revision, key)
          : draftsApi.discard(draftId, revision, key),
    );
  };

  const runMutation = async (
    operation: () => Promise<DocumentDraftMutationResult>,
  ): Promise<boolean> => {
    actionLoading.value = true;
    error.value = null;
    try {
      const result = await operation();
      await refreshAfterMutation(result);
      return true;
    } catch (cause) {
      error.value = toError(cause);
      return false;
    } finally {
      actionLoading.value = false;
    }
  };

  const refreshAfterMutation = async (
    result: Pick<DocumentDraftMutationResult, "draftId">,
  ): Promise<void> => {
    await load(false);
    await select(result.draftId);
  };

  const resetSensitive = (): void => {
    items.value = [];
    nextCursor.value = null;
    selected.value = null;
    loading.value = false;
    actionLoading.value = false;
    error.value = null;
  };

  return {
    items,
    nextCursor,
    selected,
    loading,
    actionLoading,
    error,
    statusFilter,
    load,
    select,
    save,
    rebase,
    submitReview,
    reject,
    discard,
    apply,
    resetSensitive,
  };
});

const toError = (cause: unknown): ApiClientError =>
  cause instanceof ApiClientError ? cause : new ApiClientError(0, {});
