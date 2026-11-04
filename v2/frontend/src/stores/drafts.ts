import { ref } from "vue";
import { defineStore } from "pinia";
import { draftsApi } from "../api/drafts";
import type {
  DocumentDraftApplyResult,
  DocumentDraftDetail,
  DocumentDraftItem,
  DocumentDraftMutationResult,
  DocumentDraftQueue,
  DocumentDraftQueueSummary,
  DocumentDraftStatus,
  DocumentDraftType,
  DraftBatchAction,
  DraftBatchItemResult,
} from "../types/api";
import { ApiClientError } from "../types/domain";

export interface DocumentDraftFilters {
  status: DocumentDraftStatus | null;
  type: DocumentDraftType | null;
  sourceActor: "user_ui" | "mcp" | null;
  updatedAfter: string | null;
  updatedBefore: string | null;
}

type SelectionChange = "added" | "removed" | "limit";

interface LoadOptions {
  resetSelection?: boolean;
  resetBatchResults?: boolean;
}

const emptyFilters = (): DocumentDraftFilters => ({
  status: null,
  type: null,
  sourceActor: null,
  updatedAfter: null,
  updatedBefore: null,
});

const batchKey = (action: DraftBatchAction, draftId: string): string =>
  `${action}:${draftId}`;

export const useDocumentDraftsStore = defineStore("document-drafts", () => {
  const queue = ref<DocumentDraftQueue>("pending_review");
  const filters = ref<DocumentDraftFilters>(emptyFilters());
  const items = ref<DocumentDraftItem[]>([]);
  const nextCursor = ref<string | null>(null);
  const selected = ref<DocumentDraftDetail | null>(null);
  const loading = ref(false);
  const detailLoading = ref(false);
  const actionLoading = ref(false);
  const error = ref<ApiClientError | null>(null);
  const summary = ref<DocumentDraftQueueSummary | null>(null);
  const summaryLoading = ref(false);
  const summaryError = ref<ApiClientError | null>(null);
  const selectedDraftIds = ref<string[]>([]);
  const batchRunning = ref(false);
  const batchResults = ref<Map<string, DraftBatchItemResult>>(new Map());
  const batchIdempotencyKeys = ref<Map<string, string>>(new Map());

  const loadSummary = async (): Promise<void> => {
    summaryLoading.value = true;
    summaryError.value = null;
    summary.value = null;
    try {
      summary.value = await draftsApi.summary();
    } catch (cause) {
      summaryError.value = toError(cause);
    } finally {
      summaryLoading.value = false;
    }
  };

  const load = async (
    append = false,
    options: LoadOptions = {},
  ): Promise<void> => {
    loading.value = true;
    error.value = null;
    if (!append) {
      items.value = [];
      nextCursor.value = null;
      selected.value = null;
      if (options.resetSelection !== false) {
        selectedDraftIds.value = [];
      }
      if (options.resetBatchResults !== false) {
        batchResults.value = new Map();
        batchIdempotencyKeys.value = new Map();
      }
    }
    try {
      const page = await draftsApi.list({
        queue: queue.value,
        status: filters.value.status ?? undefined,
        type: filters.value.type ?? undefined,
        sourceActor: filters.value.sourceActor ?? undefined,
        updatedAfter: filters.value.updatedAfter ?? undefined,
        updatedBefore: filters.value.updatedBefore ?? undefined,
        pageSize: 50,
        ...(append && nextCursor.value ? { cursor: nextCursor.value } : {}),
      });
      items.value = append ? [...items.value, ...page.items] : page.items;
      nextCursor.value = page.nextCursor ?? null;
    } catch (cause) {
      error.value = toError(cause);
    } finally {
      loading.value = false;
    }
  };

  const setQueue = async (next: DocumentDraftQueue): Promise<void> => {
    if (queue.value === next) {
      return;
    }
    queue.value = next;
    filters.value = emptyFilters();
    selected.value = null;
    selectedDraftIds.value = [];
    batchResults.value = new Map();
    batchIdempotencyKeys.value = new Map();
    await load();
  };

  const setFilters = async (
    next: Partial<DocumentDraftFilters>,
  ): Promise<void> => {
    filters.value = { ...filters.value, ...next };
    selected.value = null;
    selectedDraftIds.value = [];
    batchResults.value = new Map();
    batchIdempotencyKeys.value = new Map();
    await load(false, {
      resetSelection: false,
      resetBatchResults: false,
    });
  };

  const select = async (draftId: string): Promise<void> => {
    detailLoading.value = true;
    error.value = null;
    selected.value = null;
    try {
      selected.value = await draftsApi.get(draftId);
    } catch (cause) {
      error.value = toError(cause);
    } finally {
      detailLoading.value = false;
    }
  };

  const toggleSelection = (draftId: string): SelectionChange => {
    const index = selectedDraftIds.value.indexOf(draftId);
    if (index >= 0) {
      selectedDraftIds.value = selectedDraftIds.value.filter(
        (id) => id !== draftId,
      );
      return "removed";
    }
    if (selectedDraftIds.value.length >= 50) {
      return "limit";
    }
    selectedDraftIds.value = [...selectedDraftIds.value, draftId];
    return "added";
  };

  const clearSelection = (): void => {
    selectedDraftIds.value = [];
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

  const runBatch = async (action: DraftBatchAction): Promise<void> => {
    if (batchRunning.value || selectedDraftIds.value.length === 0) {
      return;
    }
    batchRunning.value = true;
    error.value = null;
    const currentDetailId = selected.value?.metadata.draftId;
    try {
      for (const draftId of [...selectedDraftIds.value]) {
        const item = items.value.find(
          (candidate) => candidate.draftId === draftId,
        );
        if (!item || !isEligible(action, item.status)) {
          setBatchResult(draftId, {
            draftId,
            action,
            kind: "not_editable",
            errorCode: "draft_not_editable",
            message: "当前状态不允许执行该操作。",
          });
          continue;
        }

        const keyId = batchKey(action, draftId);
        let key = batchIdempotencyKeys.value.get(keyId);
        if (!key) {
          key = crypto.randomUUID();
          batchIdempotencyKeys.value = new Map(batchIdempotencyKeys.value).set(
            keyId,
            key,
          );
        }

        try {
          await invokeBatchAction(action, draftId, item.draftRevision, key);
          setBatchResult(draftId, { draftId, action, kind: "success" });
          selectedDraftIds.value = selectedDraftIds.value.filter(
            (id) => id !== draftId,
          );
          const remainingKeys = new Map(batchIdempotencyKeys.value);
          remainingKeys.delete(keyId);
          batchIdempotencyKeys.value = remainingKeys;
        } catch (cause) {
          const failure = toError(cause);
          setBatchResult(draftId, {
            draftId,
            action,
            kind:
              failure.code === "draft_revision_conflict"
                ? "revision_conflict"
                : failure.code === "draft_not_editable"
                  ? "not_editable"
                  : "failed",
            errorCode: failure.code,
            message: failure.message,
          });
        }
      }

      await Promise.all([
        loadSummary(),
        load(false, {
          resetSelection: false,
          resetBatchResults: false,
        }),
      ]);
      if (
        currentDetailId &&
        items.value.some((item) => item.draftId === currentDetailId)
      ) {
        await select(currentDetailId);
      } else {
        selected.value = null;
      }
    } finally {
      batchRunning.value = false;
    }
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
    await Promise.all([
      loadSummary(),
      load(false, {
        resetSelection: false,
        resetBatchResults: false,
      }),
    ]);
    await select(result.draftId);
  };

  const setBatchResult = (
    draftId: string,
    result: DraftBatchItemResult,
  ): void => {
    batchResults.value = new Map(batchResults.value).set(draftId, result);
  };

  const resetSensitive = (): void => {
    queue.value = "pending_review";
    filters.value = emptyFilters();
    items.value = [];
    nextCursor.value = null;
    selected.value = null;
    loading.value = false;
    detailLoading.value = false;
    actionLoading.value = false;
    error.value = null;
    summary.value = null;
    summaryLoading.value = false;
    summaryError.value = null;
    selectedDraftIds.value = [];
    batchRunning.value = false;
    batchResults.value = new Map();
    batchIdempotencyKeys.value = new Map();
  };

  return {
    queue,
    filters,
    items,
    nextCursor,
    selected,
    loading,
    detailLoading,
    actionLoading,
    error,
    summary,
    summaryLoading,
    summaryError,
    selectedDraftIds,
    batchRunning,
    batchResults,
    batchIdempotencyKeys,
    loadSummary,
    load,
    setQueue,
    setFilters,
    select,
    toggleSelection,
    clearSelection,
    save,
    rebase,
    submitReview,
    reject,
    discard,
    apply,
    runBatch,
    resetSensitive,
  };
});

const invokeBatchAction = (
  action: DraftBatchAction,
  draftId: string,
  revision: number,
  idempotencyKey: string,
): Promise<DocumentDraftMutationResult> => {
  switch (action) {
    case "submit_review":
      return draftsApi.submitReview(draftId, revision, idempotencyKey);
    case "reject":
      return draftsApi.reject(draftId, revision, idempotencyKey);
    case "discard":
      return draftsApi.discard(draftId, revision, idempotencyKey);
  }
};

const isEligible = (
  action: DraftBatchAction,
  status: DocumentDraftStatus,
): boolean =>
  action === "submit_review"
    ? status === "draft"
    : action === "reject"
      ? status === "pending_review"
      : status === "draft" ||
        status === "pending_review" ||
        status === "conflict";

const toError = (cause: unknown): ApiClientError =>
  cause instanceof ApiClientError ? cause : new ApiClientError(0, {});
