import { computed, ref } from "vue";
import { defineStore } from "pinia";
import { documentApi, revisionApi } from "../api";
import {
  registerDirtyStateProvider,
  registerSensitiveStore,
} from "../session/lifecycle";
import type {
  BacklinkItem,
  CreateDocumentRequest,
  DocumentMap,
  DocumentMetadata,
  DocumentWriteResult,
  RevisionContent,
  RevisionItem,
  UnlinkedMentionItem,
} from "../types/api";
import { ApiClientError, StaleSessionResponseError } from "../types/domain";

export class StaleDocumentOperationError extends Error {
  constructor() {
    super("The document operation belongs to an inactive document.");
    this.name = "StaleDocumentOperationError";
  }
}

interface DocumentOperationContext {
  documentId: string;
  documentGeneration: number;
  operationGeneration: number;
  operationKind:
    "conflict" | "reload" | "compare" | "restore" | "create" | "delete";
}

export interface DocumentDraft {
  title: string;
  virtualPath: string;
  content: string;
  tags: string[];
}

export interface SaveConflict {
  currentRevision: number;
  mode: "notice" | "compare";
  latestMetadata: DocumentMetadata | null;
  latest: DocumentDraft | null;
}

export const useDocumentStore = defineStore("document", () => {
  const documentId = ref<string | null>(null);
  const metadata = ref<DocumentMetadata | null>(null);
  const loadedRevision = ref<number | null>(null);
  const savedDraft = ref<DocumentDraft | null>(null);
  const draft = ref<DocumentDraft | null>(null);
  const dirty = ref(false);
  const loading = ref(false);
  const error = ref<ApiClientError | null>(null);
  const map = ref<DocumentMap | null>(null);
  const backlinks = ref<BacklinkItem[]>([]);
  const mentions = ref<UnlinkedMentionItem[]>([]);
  const revisions = ref<RevisionItem[]>([]);
  const revisionContents = ref<Record<string, RevisionContent>>({});
  const backlinkCursor = ref<string | null>(null);
  const mentionCursor = ref<string | null>(null);
  const revisionCursor = ref<string | null>(null);
  const selectedRevisionIds = ref<string[]>([]);
  const revisionDiff = ref("");
  const revisionCompareLoading = ref(false);
  const revisionCompareError = ref<ApiClientError | null>(null);
  const saveState = ref<"idle" | "saving" | "error">("idle");
  const refreshWarning = ref<string | null>(null);
  const conflict = ref<SaveConflict | null>(null);
  let requestGeneration = 0;
  let backlinkRequestGeneration = 0;
  let mentionRequestGeneration = 0;
  let revisionRequestGeneration = 0;
  let conflictRequestGeneration = 0;
  let reloadRequestGeneration = 0;
  let revisionCompareRequestGeneration = 0;
  let restoreRequestGeneration = 0;
  let createRequestGeneration = 0;
  let deleteRequestGeneration = 0;
  let documentSwitchInProgress = false;

  const hasDocument = computed(() => documentId.value !== null);

  const invalidateDocumentOperations = (): void => {
    conflictRequestGeneration++;
    reloadRequestGeneration++;
    revisionCompareRequestGeneration++;
    restoreRequestGeneration++;
    createRequestGeneration++;
    deleteRequestGeneration++;
    backlinkRequestGeneration++;
    mentionRequestGeneration++;
    revisionRequestGeneration++;
    revisionCompareLoading.value = false;
    revisionCompareError.value = null;
    conflict.value = null;
    saveState.value = "idle";
    refreshWarning.value = null;
  };

  const isCurrent = (context: DocumentOperationContext): boolean =>
    !documentSwitchInProgress &&
    documentId.value === context.documentId &&
    requestGeneration === context.documentGeneration;

  const operationCounter = (
    kind: DocumentOperationContext["operationKind"],
  ): number =>
    kind === "conflict"
      ? conflictRequestGeneration
      : kind === "reload"
        ? reloadRequestGeneration
        : kind === "compare"
          ? revisionCompareRequestGeneration
          : kind === "restore"
            ? restoreRequestGeneration
            : kind === "create"
              ? createRequestGeneration
              : deleteRequestGeneration;

  const isCurrentOperation = (context: DocumentOperationContext): boolean =>
    isCurrent(context) &&
    operationCounter(context.operationKind) === context.operationGeneration;

  const assertCurrent = (context: DocumentOperationContext): void => {
    if (!isCurrentOperation(context)) {
      throw new StaleDocumentOperationError();
    }
  };

  const assertDocumentCurrent = (context: DocumentOperationContext): void => {
    if (!isCurrent(context)) {
      throw new StaleDocumentOperationError();
    }
  };

  const isStaleError = (value: unknown): boolean =>
    value instanceof StaleSessionResponseError ||
    value instanceof StaleDocumentOperationError;

  const incrementOperation = (
    kind: DocumentOperationContext["operationKind"],
  ): number => {
    if (kind === "conflict") {
      return ++conflictRequestGeneration;
    }
    if (kind === "reload") {
      return ++reloadRequestGeneration;
    }
    if (kind === "compare") {
      return ++revisionCompareRequestGeneration;
    }
    if (kind === "restore") {
      return ++restoreRequestGeneration;
    }
    if (kind === "create") {
      return ++createRequestGeneration;
    }
    return ++deleteRequestGeneration;
  };

  const captureOperation = (
    kind: DocumentOperationContext["operationKind"],
  ): DocumentOperationContext => {
    const id = documentId.value;
    if (!id || documentSwitchInProgress) {
      throw new StaleDocumentOperationError();
    }
    return {
      documentId: id,
      documentGeneration: requestGeneration,
      operationGeneration: incrementOperation(kind),
      operationKind: kind,
    };
  };

  const copyDraft = (value: DocumentDraft): DocumentDraft => ({
    title: value.title,
    virtualPath: value.virtualPath,
    content: value.content,
    tags: [...value.tags],
  });

  const setFromMetadata = (value: DocumentMetadata, content: string): void => {
    const nextDraft: DocumentDraft = {
      title: value.title,
      virtualPath: value.virtualPath,
      content,
      tags: [...value.tags],
    };
    metadata.value = value;
    loadedRevision.value = value.revision;
    savedDraft.value = copyDraft(nextDraft);
    draft.value = copyDraft(nextDraft);
    dirty.value = false;
  };

  const open = async (id: string): Promise<void> => {
    const generation = ++requestGeneration;
    invalidateDocumentOperations();
    documentSwitchInProgress = true;
    loading.value = true;
    error.value = null;
    refreshWarning.value = null;
    try {
      const info = await documentApi.metadata(id);
      if (generation !== requestGeneration) {
        throw new StaleDocumentOperationError();
      }
      let offset = 0;
      const chunks: string[] = [];
      while (true) {
        const chunk = await documentApi.content(id, offset, 65_536);
        if (generation !== requestGeneration) {
          throw new StaleDocumentOperationError();
        }
        chunks.push(chunk.content);
        if (chunk.done) {
          break;
        }
        offset = chunk.nextOffset;
      }
      const documentMap = await documentApi.map(id);
      if (generation !== requestGeneration) {
        throw new StaleDocumentOperationError();
      }

      documentId.value = id;
      setFromMetadata(info, chunks.join(""));
      map.value = documentMap;
      backlinks.value = [];
      mentions.value = [];
      revisions.value = [];
      revisionContents.value = {};
      backlinkCursor.value = null;
      mentionCursor.value = null;
      revisionCursor.value = null;
      selectedRevisionIds.value = [];
      revisionDiff.value = "";
      saveState.value = "idle";
      refreshWarning.value = null;
      conflict.value = null;
    } catch (caught) {
      if (
        caught instanceof StaleSessionResponseError ||
        caught instanceof StaleDocumentOperationError
      ) {
        return;
      }
      if (generation === requestGeneration) {
        error.value =
          caught instanceof ApiClientError
            ? caught
            : new ApiClientError(0, {
                code: "network_error",
                message: "无法读取文档。",
              });
      }
    } finally {
      if (generation === requestGeneration) {
        documentSwitchInProgress = false;
        loading.value = false;
      }
    }
  };

  const create = async (
    request: CreateDocumentRequest,
  ): Promise<DocumentWriteResult> => {
    const documentGeneration = requestGeneration;
    const previousDocumentId = documentId.value;
    const operationGeneration = ++createRequestGeneration;
    const result = await documentApi.create(request);
    if (
      requestGeneration === documentGeneration &&
      documentId.value === previousDocumentId &&
      !documentSwitchInProgress &&
      createRequestGeneration === operationGeneration
    ) {
      resetSensitive();
      documentId.value = result.documentId;
      loadedRevision.value = result.revision;
    }
    return result;
  };

  const updateDraft = (update: Partial<DocumentDraft>): void => {
    if (!draft.value) {
      return;
    }
    draft.value = {
      ...draft.value,
      ...update,
      tags: update.tags ? [...update.tags] : draft.value.tags,
    };
    dirty.value =
      savedDraft.value === null ||
      draft.value.title !== savedDraft.value.title ||
      draft.value.virtualPath !== savedDraft.value.virtualPath ||
      draft.value.content !== savedDraft.value.content ||
      draft.value.tags.join("\u001F") !== savedDraft.value.tags.join("\u001F");
  };

  const readAllContent = async (
    id: string,
    guard?: () => void,
  ): Promise<string> => {
    let offset = 0;
    const chunks: string[] = [];
    while (true) {
      guard?.();
      const chunk = await documentApi.content(id, offset, 65_536);
      guard?.();
      chunks.push(chunk.content);
      if (chunk.done) {
        return chunks.join("");
      }
      offset = chunk.nextOffset;
    }
  };

  const save = async (): Promise<DocumentWriteResult | null> => {
    const id = documentId.value;
    const revision = loadedRevision.value;
    const current = draft.value;
    const info = metadata.value;
    if (
      !id ||
      revision === null ||
      !current ||
      !info ||
      saveState.value === "saving"
    ) {
      return null;
    }
    if (conflict.value) {
      throw new ApiClientError(409, {
        code: "document_revision_conflict",
        message: "请先处理当前 Revision 冲突。",
      });
    }

    saveState.value = "saving";
    error.value = null;
    refreshWarning.value = null;
    const documentGeneration = requestGeneration;
    try {
      const result = await documentApi.update(id, revision, {
        title: current.title,
        virtualPath: current.virtualPath,
        content: current.content,
        ...(info.format === "txt" ? { tags: current.tags } : {}),
        updateBacklinks: false,
      });
      if (documentGeneration === requestGeneration && documentId.value === id) {
        metadata.value = {
          ...info,
          title: current.title,
          virtualPath: result.virtualPath,
          revision: result.revision,
          tags: [...current.tags],
          updatedAt: result.committedAt,
        };
        loadedRevision.value = result.revision;
        savedDraft.value = copyDraft(current);
        draft.value = copyDraft(current);
        dirty.value = false;
        conflict.value = null;
        saveState.value = "idle";
      }

      try {
        const [latestMetadata, latestMap] = await Promise.all([
          documentApi.metadata(id),
          documentApi.map(id),
        ]);
        if (
          documentGeneration === requestGeneration &&
          documentId.value === id &&
          loadedRevision.value === result.revision
        ) {
          metadata.value = latestMetadata;
          map.value = latestMap;
        }
      } catch (caught) {
        if (
          documentGeneration === requestGeneration &&
          documentId.value === id &&
          !(caught instanceof StaleSessionResponseError)
        ) {
          refreshWarning.value = "文档已保存，但最新文档信息暂时无法刷新。";
        }
      }
      return result;
    } catch (caught) {
      if (documentGeneration === requestGeneration && documentId.value === id) {
        if (
          caught instanceof ApiClientError &&
          caught.code === "document_revision_conflict"
        ) {
          const value = caught.details.currentRevision;
          conflict.value = {
            currentRevision: typeof value === "number" ? value : 0,
            mode: "notice",
            latestMetadata: null,
            latest: null,
          };
        } else {
          error.value =
            caught instanceof ApiClientError
              ? caught
              : new ApiClientError(0, {
                  code: "network_error",
                  message: "保存失败。",
                });
        }
        saveState.value = "error";
      }
      throw caught;
    }
  };

  const loadLatestConflict = async (): Promise<void> => {
    let context: DocumentOperationContext;
    try {
      context = captureOperation("conflict");
      const latestMetadata = await documentApi.metadata(context.documentId);
      assertCurrent(context);
      const latestContent = await readAllContent(context.documentId, () =>
        assertCurrent(context),
      );
      assertCurrent(context);
      conflict.value = {
        currentRevision: latestMetadata.revision,
        mode: "compare",
        latestMetadata,
        latest: {
          title: latestMetadata.title,
          virtualPath: latestMetadata.virtualPath,
          content: latestContent,
          tags: [...latestMetadata.tags],
        },
      };
    } catch (caught) {
      if (isStaleError(caught)) {
        return;
      }
      throw caught;
    }
  };

  const reloadLatestWithContext = async (
    context: DocumentOperationContext,
    createContext: boolean,
  ): Promise<void> => {
    const active = createContext ? captureOperation("reload") : context;
    const latestMetadata = await documentApi.metadata(active.documentId);
    assertCurrent(active);
    const content = await readAllContent(active.documentId, () =>
      assertCurrent(active),
    );
    assertCurrent(active);
    setFromMetadata(latestMetadata, content);
    conflict.value = null;
    saveState.value = "idle";
  };

  const reloadLatest = async (): Promise<void> => {
    let context: DocumentOperationContext;
    try {
      context = captureOperation("reload");
      await reloadLatestWithContext(context, false);
      assertCurrent(context);
    } catch (caught) {
      if (isStaleError(caught)) {
        return;
      }
      throw caught;
    }
  };

  const discardLocalConflict = (): void => {
    const latest = conflict.value?.latest;
    const latestMetadata = conflict.value?.latestMetadata;
    if (latest && latestMetadata) {
      setFromMetadata(latestMetadata, latest.content);
    } else if (savedDraft.value) {
      draft.value = copyDraft(savedDraft.value);
      dirty.value = false;
    }
    conflict.value = null;
    saveState.value = "idle";
  };

  const adoptLatestRevision = (): void => {
    const current = conflict.value;
    if (!current) {
      return;
    }
    loadedRevision.value = current.currentRevision;
    if (current.latestMetadata) {
      metadata.value = current.latestMetadata;
      if (
        current.latestMetadata.format === "txt" &&
        draft.value &&
        current.latest
      ) {
        draft.value = {
          ...draft.value,
          tags: [...current.latest.tags],
        };
      }
    }
    conflict.value = null;
    dirty.value = true;
    saveState.value = "idle";
  };

  const loadBacklinks = async (
    append = false,
    inheritedContext?: DocumentOperationContext,
  ): Promise<void> => {
    const id = inheritedContext?.documentId ?? documentId.value;
    if (!id || (append && !backlinkCursor.value)) {
      return;
    }
    const generation = ++backlinkRequestGeneration;
    const documentGeneration =
      inheritedContext?.documentGeneration ?? requestGeneration;
    const page = await documentApi.backlinks(
      id,
      append ? (backlinkCursor.value ?? undefined) : undefined,
    );
    if (
      generation !== backlinkRequestGeneration ||
      documentGeneration !== requestGeneration ||
      documentId.value !== id ||
      (inheritedContext !== undefined && !isCurrentOperation(inheritedContext))
    ) {
      return;
    }
    backlinks.value = append ? [...backlinks.value, ...page.items] : page.items;
    backlinkCursor.value = page.nextCursor ?? null;
  };

  const loadMentions = async (
    append = false,
    inheritedContext?: DocumentOperationContext,
  ): Promise<void> => {
    const id = inheritedContext?.documentId ?? documentId.value;
    if (!id || (append && !mentionCursor.value)) {
      return;
    }
    const generation = ++mentionRequestGeneration;
    const documentGeneration =
      inheritedContext?.documentGeneration ?? requestGeneration;
    const page = await documentApi.unlinkedMentions(
      id,
      append ? (mentionCursor.value ?? undefined) : undefined,
    );
    if (
      generation !== mentionRequestGeneration ||
      documentGeneration !== requestGeneration ||
      documentId.value !== id ||
      (inheritedContext !== undefined && !isCurrentOperation(inheritedContext))
    ) {
      return;
    }
    mentions.value = append ? [...mentions.value, ...page.items] : page.items;
    mentionCursor.value = page.nextCursor ?? null;
  };

  const loadRevisions = async (
    append = false,
    inheritedContext?: DocumentOperationContext,
  ): Promise<void> => {
    const id = inheritedContext?.documentId ?? documentId.value;
    if (!id || (append && !revisionCursor.value)) {
      return;
    }
    const generation = ++revisionRequestGeneration;
    const documentGeneration =
      inheritedContext?.documentGeneration ?? requestGeneration;
    const page = await revisionApi.list(
      id,
      append ? (revisionCursor.value ?? undefined) : undefined,
    );
    if (
      generation !== revisionRequestGeneration ||
      documentGeneration !== requestGeneration ||
      documentId.value !== id ||
      (inheritedContext !== undefined && !isCurrentOperation(inheritedContext))
    ) {
      return;
    }
    revisions.value = append ? [...revisions.value, ...page.items] : page.items;
    revisionCursor.value = page.nextCursor ?? null;
  };

  const loadInspector = async (
    inheritedContext?: DocumentOperationContext,
  ): Promise<void> => {
    await Promise.all([
      loadBacklinks(false, inheritedContext),
      loadMentions(false, inheritedContext),
      loadRevisions(false, inheritedContext),
    ]);
  };

  const toggleRevisionSelection = (revisionId: string): void => {
    revisionCompareRequestGeneration++;
    revisionDiff.value = "";
    revisionCompareError.value = null;
    revisionCompareLoading.value = false;
    if (selectedRevisionIds.value.includes(revisionId)) {
      selectedRevisionIds.value = selectedRevisionIds.value.filter(
        (id) => id !== revisionId,
      );
      return;
    }
    selectedRevisionIds.value = [
      ...selectedRevisionIds.value.slice(-1),
      revisionId,
    ];
  };

  const compareSelectedRevisions = async (): Promise<void> => {
    if (selectedRevisionIds.value.length !== 2) {
      return;
    }
    let context: DocumentOperationContext;
    try {
      context = captureOperation("compare");
    } catch (caught) {
      if (isStaleError(caught)) {
        return;
      }
      throw caught;
    }
    const revisionIds = [...selectedRevisionIds.value];
    revisionCompareLoading.value = true;
    revisionCompareError.value = null;
    try {
      const contents: RevisionContent[] = [];
      for (const revisionId of revisionIds) {
        assertCurrent(context);
        let content = revisionContents.value[revisionId];
        if (!content) {
          content = await revisionApi.content(context.documentId, revisionId);
          assertDocumentCurrent(context);
          revisionContents.value[revisionId] = content;
        }
        contents.push(content);
      }
      const { createRevisionDiff } = await import("../utils/revision-diff");
      assertDocumentCurrent(context);
      if (!isCurrentOperation(context)) {
        return;
      }
      revisionDiff.value = createRevisionDiff(contents[0]!, contents[1]!);
    } catch (caught) {
      if (isStaleError(caught) || !isCurrentOperation(context)) {
        return;
      }
      revisionCompareError.value =
        caught instanceof ApiClientError
          ? caught
          : new ApiClientError(0, {
              code: "network_error",
              message: "无法读取历史版本。",
            });
    } finally {
      if (isCurrentOperation(context)) {
        revisionCompareLoading.value = false;
      }
    }
  };

  const restoreRevision = async (revisionId: string): Promise<void> => {
    const revision = loadedRevision.value;
    let context: DocumentOperationContext;
    try {
      context = captureOperation("restore");
    } catch (caught) {
      if (isStaleError(caught)) {
        return;
      }
      throw caught;
    }
    if (revision === null) {
      return;
    }
    try {
      await revisionApi.restore(context.documentId, revisionId, revision);
      assertCurrent(context);
      await reloadLatestWithContext(context, false);
      assertCurrent(context);
      await loadInspector(context);
      assertCurrent(context);
    } catch (caught) {
      if (isStaleError(caught)) {
        return;
      }
      throw caught;
    }
  };

  const deleteDocument = async (): Promise<void> => {
    const revision = loadedRevision.value;
    let context: DocumentOperationContext;
    try {
      context = captureOperation("delete");
    } catch (caught) {
      if (isStaleError(caught)) {
        return;
      }
      throw caught;
    }
    if (revision === null) {
      return;
    }
    await documentApi.delete(context.documentId, revision);
    if (isCurrentOperation(context)) {
      resetSensitive();
    }
  };

  const resetSensitive = (): void => {
    requestGeneration++;
    invalidateDocumentOperations();
    documentSwitchInProgress = false;
    documentId.value = null;
    metadata.value = null;
    loadedRevision.value = null;
    savedDraft.value = null;
    draft.value = null;
    dirty.value = false;
    loading.value = false;
    error.value = null;
    map.value = null;
    backlinks.value = [];
    mentions.value = [];
    revisions.value = [];
    revisionContents.value = {};
    backlinkCursor.value = null;
    mentionCursor.value = null;
    revisionCursor.value = null;
    selectedRevisionIds.value = [];
    revisionDiff.value = "";
    revisionCompareLoading.value = false;
    revisionCompareError.value = null;
    saveState.value = "idle";
    refreshWarning.value = null;
    conflict.value = null;
  };

  registerSensitiveStore({ resetSensitive });
  registerDirtyStateProvider(() => dirty.value);

  return {
    documentId,
    metadata,
    loadedRevision,
    savedDraft,
    draft,
    dirty,
    loading,
    error,
    map,
    backlinks,
    mentions,
    revisions,
    revisionContents,
    selectedRevisionIds,
    revisionDiff,
    revisionCompareLoading,
    revisionCompareError,
    backlinkCursor,
    mentionCursor,
    revisionCursor,
    saveState,
    refreshWarning,
    conflict,
    hasDocument,
    open,
    create,
    updateDraft,
    save,
    loadLatestConflict,
    reloadLatest,
    discardLocalConflict,
    adoptLatestRevision,
    loadBacklinks,
    loadMentions,
    loadRevisions,
    loadInspector,
    toggleRevisionSelection,
    compareSelectedRevisions,
    restoreRevision,
    deleteDocument,
    resetSensitive,
  };
});
