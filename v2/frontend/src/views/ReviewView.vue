<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { useRouter } from "vue-router";
import {
  NAlert,
  NButton,
  NButtonGroup,
  NCheckbox,
  NDatePicker,
  NEmpty,
  NIcon,
  NInput,
  NPopconfirm,
  NSelect,
  NSpin,
  NTag,
  useMessage,
} from "naive-ui";
import { Link2, RefreshCw, ShieldAlert, ShieldCheck } from "@lucide/vue";
import RelationSuggestionPanel from "../components/review/RelationSuggestionPanel.vue";
import { documentApi } from "../api/documents";
import { registerSensitiveStore } from "../session/lifecycle";
import { useDocumentDraftsStore } from "../stores/drafts";
import { useWriteModeStore } from "../stores/write-mode";
import type {
  DocumentDraftFieldChange,
  DocumentDraftItem,
  DocumentDraftQueue,
  DocumentDraftStatus,
  DraftBatchAction,
  DraftBatchItemResult,
} from "../types/api";
import type { VaultFormat } from "../types/domain";
import {
  buildEditedUpdatePayload,
  reviewContextFromPayload,
} from "../utils/draft-payload";
import { writeModeDisplayState } from "../utils/write-mode-state";

const drafts = useDocumentDraftsStore();
const writeMode = useWriteModeStore();
const router = useRouter();
const message = useMessage();
const title = ref("");
const virtualPath = ref("");
const format = ref<VaultFormat>("md");
const content = ref("");
const tags = ref("");
const reviewSection = ref<"documents" | "relations">("documents");
let unregisterDrafts: (() => void) | null = null;
let unregisterWriteMode: (() => void) | null = null;

const queueOptions: Array<{
  key: DocumentDraftQueue;
  label: string;
}> = [
  { key: "pending_review", label: "待审核" },
  { key: "working", label: "整理中" },
  { key: "history", label: "历史" },
];

const typeOptions = [
  { label: "创建", value: "create" },
  { label: "更新", value: "update" },
  { label: "移至回收站", value: "trash" },
] as const;

const actorOptions = [
  { label: "Web UI", value: "user_ui" },
  { label: "MCP", value: "mcp" },
] as const;

const statusLabels: Record<DocumentDraftStatus, string> = {
  draft: "草稿",
  pending_review: "待审核",
  conflict: "冲突",
  applied: "已提交",
  rejected: "已拒绝",
  discarded: "已放弃",
  expired: "已过期",
};

const currentStatusOptions = computed(() =>
  drafts.queue === "history"
    ? (["applied", "rejected", "discarded", "expired"] as const).map(
        (status) => ({ label: statusLabels[status], value: status }),
      )
    : drafts.queue === "working"
      ? (["draft", "conflict"] as const).map((status) => ({
          label: statusLabels[status],
          value: status,
        }))
      : [{ label: statusLabels.pending_review, value: "pending_review" }],
);

const selected = computed(() => drafts.selected);
const isTerminal = computed(
  () =>
    selected.value?.metadata.status === "applied" ||
    selected.value?.metadata.status === "rejected" ||
    selected.value?.metadata.status === "discarded" ||
    selected.value?.metadata.status === "expired",
);
const editable = computed(
  () =>
    !isTerminal.value &&
    selected.value?.metadata.draftType !== "trash" &&
    (selected.value?.metadata.status === "draft" ||
      selected.value?.metadata.status === "pending_review"),
);
const contentEditable = computed(
  () =>
    editable.value &&
    (selected.value?.metadata.draftType === "create" ||
      selected.value?.preview.bodyEdit.canSave === true),
);
const canRebase = computed(
  () => selected.value?.metadata.status === "conflict",
);
const canApply = computed(
  () => selected.value?.metadata.status === "pending_review",
);
const canDiscard = computed(
  () =>
    selected.value?.metadata.status === "draft" ||
    selected.value?.metadata.status === "pending_review" ||
    selected.value?.metadata.status === "conflict",
);
const writeModeState = computed(() =>
  writeModeDisplayState(
    writeMode.status,
    writeMode.loading,
    writeMode.error !== null,
  ),
);
const selectedCount = computed(() => drafts.selectedDraftIds.length);
const batchResultList = computed(() =>
  Array.from(drafts.batchResults.values()),
);
const allVisibleSelected = computed(
  () =>
    drafts.items.length > 0 &&
    drafts.items.every((item) =>
      drafts.selectedDraftIds.includes(item.draftId),
    ),
);
const canBatchSubmit = computed(() =>
  drafts.items.some(
    (item) =>
      drafts.selectedDraftIds.includes(item.draftId) && item.status === "draft",
  ),
);
const canBatchReject = computed(() =>
  drafts.items.some(
    (item) =>
      drafts.selectedDraftIds.includes(item.draftId) &&
      item.status === "pending_review",
  ),
);
const canBatchDiscard = computed(() =>
  drafts.items.some(
    (item) =>
      drafts.selectedDraftIds.includes(item.draftId) &&
      (item.status === "draft" ||
        item.status === "pending_review" ||
        item.status === "conflict"),
  ),
);

watch(
  () => drafts.selected,
  (detail) => {
    if (!detail) {
      title.value = "";
      virtualPath.value = "";
      format.value = "md";
      content.value = "";
      tags.value = "";
      return;
    }
    title.value = detail.metadata.title ?? "";
    virtualPath.value = detail.metadata.virtualPath ?? "";
    format.value = detail.metadata.format ?? "md";
    const payloadContent = detail.payload.content;
    content.value =
      typeof payloadContent === "string"
        ? payloadContent
        : detail.preview.content;
    tags.value = detail.preview.tags.join(", ");
  },
);

onMounted(() => {
  unregisterDrafts = registerSensitiveStore(drafts);
  unregisterWriteMode = registerSensitiveStore(writeMode);
  void Promise.all([drafts.loadSummary(), drafts.load(), writeMode.load()]);
});

onBeforeUnmount(() => {
  unregisterDrafts?.();
  unregisterWriteMode?.();
  unregisterDrafts = null;
  unregisterWriteMode = null;
  drafts.resetSensitive();
  writeMode.resetSensitive();
});

const refreshWorkspace = async (): Promise<void> => {
  await Promise.all([drafts.loadSummary(), drafts.load()]);
};

const queueCount = (next: DocumentDraftQueue): string => {
  if (!drafts.summary) {
    return "-";
  }
  return String(
    next === "pending_review"
      ? drafts.summary.pendingReview
      : next === "working"
        ? drafts.summary.working
        : drafts.summary.history,
  );
};

const selectDraft = async (item: DocumentDraftItem): Promise<void> => {
  await drafts.select(item.draftId);
};

const toggleDraft = (draftId: string): void => {
  if (drafts.toggleSelection(draftId) === "limit") {
    message.warning("一次最多选择 50 份草稿。");
  }
};

const toggleAllVisible = (checked: boolean): void => {
  if (!checked) {
    drafts.clearSelection();
    return;
  }
  for (const item of drafts.items) {
    if (!drafts.selectedDraftIds.includes(item.draftId)) {
      if (drafts.toggleSelection(item.draftId) === "limit") {
        message.warning("一次最多选择 50 份草稿。");
        break;
      }
    }
  }
};

const changeQueue = async (next: DocumentDraftQueue): Promise<void> => {
  await drafts.setQueue(next);
};

const changeStatusFilter = async (
  value: DocumentDraftStatus | null,
): Promise<void> => {
  await drafts.setFilters({ status: value });
};

const changeTypeFilter = async (
  value: "create" | "update" | "trash" | null,
): Promise<void> => {
  await drafts.setFilters({ type: value });
};

const changeActorFilter = async (
  value: "user_ui" | "mcp" | null,
): Promise<void> => {
  await drafts.setFilters({ sourceActor: value });
};

const changeAfterFilter = async (value: number | null): Promise<void> => {
  await drafts.setFilters({
    updatedAfter: value === null ? null : new Date(value).toISOString(),
  });
};

const changeBeforeFilter = async (value: number | null): Promise<void> => {
  await drafts.setFilters({
    updatedBefore: value === null ? null : new Date(value).toISOString(),
  });
};

const dateValue = (value: string | null): number | null =>
  value === null ? null : new Date(value).getTime();

const parseTags = (): string[] =>
  tags.value
    .split(",")
    .map((tag) => tag.trim())
    .filter(Boolean);

const bodyEditReason = (reason?: string | null): string =>
  reason === "mixed_body_operation_unsafe"
    ? "非正文变更无法安全应用到当前版本，请先处理该草稿。"
    : "当前草稿无法安全保存正文编辑。";

const save = async (): Promise<void> => {
  if (!selected.value) {
    return;
  }
  if (
    selected.value.metadata.draftType !== "create" &&
    !contentEditable.value
  ) {
    message.warning(bodyEditReason(selected.value.preview.bodyEdit.reason));
    return;
  }
  const reviewContext = reviewContextFromPayload(selected.value.payload);
  const payload =
    selected.value.metadata.draftType === "create"
      ? {
          title: title.value.trim(),
          virtualPath: virtualPath.value.trim() || null,
          format: format.value,
          content: content.value,
          tags: parseTags(),
          ...(reviewContext === undefined ? {} : { reviewContext }),
        }
      : buildEditedUpdatePayload(selected.value, content.value);
  if (await drafts.save(payload)) {
    message.success("草稿已保存。");
  } else {
    message.error(drafts.error?.message ?? "草稿保存失败。");
  }
};

const submitReview = async (): Promise<void> => {
  if (await drafts.submitReview()) {
    message.success("草稿已进入待审核。");
  } else {
    message.error(drafts.error?.message ?? "送审失败。");
  }
};

const reject = async (): Promise<void> => {
  if (await drafts.reject()) {
    message.success("草稿已拒绝。");
  } else {
    message.error(drafts.error?.message ?? "拒绝失败。");
  }
};

const discard = async (): Promise<void> => {
  if (await drafts.discard()) {
    message.success("草稿已放弃。");
  } else {
    message.error(drafts.error?.message ?? "放弃失败。");
  }
};

const apply = async (): Promise<void> => {
  const result = await drafts.apply();
  if (result) {
    message.success(
      `已提交为正式文档 R${result.documentWriteResult.revision}。`,
    );
  } else {
    message.error(drafts.error?.message ?? "提交失败。");
  }
};

const rebase = async (): Promise<void> => {
  const targetId = selected.value?.metadata.targetDocumentId;
  if (!targetId) {
    return;
  }
  try {
    const metadata = await documentApi.metadata(targetId);
    if (await drafts.rebase(metadata.revision)) {
      message.success("草稿已基于最新版本重建。");
    } else {
      message.error(drafts.error?.message ?? "重建失败。");
    }
  } catch {
    message.error("无法读取目标文档当前版本。");
  }
};

const runBatch = async (action: DraftBatchAction): Promise<void> => {
  const before = new Map(
    batchResultList.value.map((result) => [result.draftId, result.kind]),
  );
  await drafts.runBatch(action);
  const completed = Array.from(drafts.batchResults.values()).filter(
    (result) => before.get(result.draftId) !== result.kind,
  );
  const succeeded = completed.filter(
    (result) => result.kind === "success",
  ).length;
  const failed = completed.length - succeeded;
  if (failed === 0) {
    message.success(`已完成 ${succeeded} 项。`);
  } else {
    message.warning(`成功 ${succeeded} 项，失败 ${failed} 项。`);
  }
};

const statusLabel = (status: DocumentDraftStatus): string =>
  statusLabels[status];

const batchResultLabel = (result: DraftBatchItemResult): string =>
  result.kind === "success"
    ? "成功"
    : result.kind === "revision_conflict"
      ? "版本冲突"
      : result.kind === "not_editable"
        ? "状态不允许"
        : "执行失败";

const changeLabel = (change: DocumentDraftFieldChange): string => {
  if (change.fieldKind === "frontmatter") {
    return `属性 ${change.key ?? ""}`;
  }
  return (
    {
      title: "标题",
      virtual_path: "路径",
      format: "格式",
      tags: "标签",
      body: "正文",
      trash: "移至回收站",
      frontmatter: "属性",
    } as const
  )[change.fieldKind];
};

const changeValue = (
  change: DocumentDraftFieldChange,
  side: "before" | "after",
): string => {
  const exists = side === "before" ? change.beforeExists : change.afterExists;
  if (!exists) {
    return "不存在";
  }
  const value = (side === "before" ? change.before : change.after) ?? "null";
  const truncated =
    side === "before" ? change.beforeTruncated : change.afterTruncated;
  return truncated ? `${value}...` : value;
};

const openSource = async (documentId: string): Promise<void> => {
  await router.push({ name: "document", params: { id: documentId } });
};
</script>

<template>
  <section class="review-view">
    <header class="page-heading">
      <div>
        <p>草稿审核与历史</p>
        <h1>AI 工作区</h1>
      </div>
      <NButton
        v-if="reviewSection === 'documents'"
        secondary
        :loading="drafts.loading || drafts.summaryLoading"
        @click="refreshWorkspace"
      >
        <template #icon>
          <NIcon :component="RefreshCw" aria-hidden="true" />
        </template>
        刷新
      </NButton>
    </header>

    <div class="workspace-switch">
      <NButtonGroup>
        <NButton
          :type="reviewSection === 'documents' ? 'primary' : 'default'"
          @click="reviewSection = 'documents'"
        >
          文档草稿
        </NButton>
        <NButton
          :type="reviewSection === 'relations' ? 'primary' : 'default'"
          @click="reviewSection = 'relations'"
        >
          关系建议
        </NButton>
      </NButtonGroup>
    </div>

    <RelationSuggestionPanel v-if="reviewSection === 'relations'" />

    <template v-else>
      <section class="summary-strip" aria-label="草稿队列统计">
        <div>
          <span>待审核</span>
          <strong>{{ drafts.summary?.pendingReview ?? "-" }}</strong>
        </div>
        <div>
          <span>整理中</span>
          <strong>{{ drafts.summary?.working ?? "-" }}</strong>
        </div>
        <div>
          <span>历史</span>
          <strong>{{ drafts.summary?.history ?? "-" }}</strong>
        </div>
      </section>

      <NAlert v-if="drafts.summaryError" type="error" :show-icon="false">
        {{ drafts.summaryError.message }}
        <NButton text type="primary" @click="drafts.loadSummary()"
          >重试</NButton
        >
      </NAlert>
      <NAlert v-if="drafts.error" type="error" :show-icon="false">
        {{ drafts.error.message }}
      </NAlert>

      <div class="queue-bar">
        <NButtonGroup>
          <NButton
            v-for="option in queueOptions"
            :key="option.key"
            :type="drafts.queue === option.key ? 'primary' : 'default'"
            @click="changeQueue(option.key)"
          >
            {{ option.label }}
            <span class="queue-count">{{ queueCount(option.key) }}</span>
          </NButton>
        </NButtonGroup>
        <span class="selection-count">已选 {{ selectedCount }} / 50</span>
      </div>

      <section v-if="drafts.queue !== 'pending_review'" class="filter-bar">
        <NSelect
          clearable
          placeholder="状态"
          :value="drafts.filters.status"
          :options="currentStatusOptions"
          @update:value="changeStatusFilter"
        />
        <NSelect
          clearable
          placeholder="类型"
          :value="drafts.filters.type"
          :options="[...typeOptions]"
          @update:value="changeTypeFilter"
        />
        <NSelect
          clearable
          placeholder="来源"
          :value="drafts.filters.sourceActor"
          :options="[...actorOptions]"
          @update:value="changeActorFilter"
        />
        <NDatePicker
          clearable
          type="datetime"
          placeholder="更新起"
          :value="dateValue(drafts.filters.updatedAfter)"
          @update:value="changeAfterFilter"
        />
        <NDatePicker
          clearable
          type="datetime"
          placeholder="更新止"
          :value="dateValue(drafts.filters.updatedBefore)"
          @update:value="changeBeforeFilter"
        />
      </section>

      <section v-if="selectedCount > 0" class="batch-bar">
        <NCheckbox
          :checked="allVisibleSelected"
          @update:checked="toggleAllVisible"
        >
          全选当前页
        </NCheckbox>
        <div class="batch-actions">
          <NButton
            size="small"
            :disabled="!canBatchSubmit"
            :loading="drafts.batchRunning"
            @click="runBatch('submit_review')"
          >
            批量送审
          </NButton>
          <NPopconfirm @positive-click="runBatch('reject')">
            <template #trigger>
              <NButton
                size="small"
                type="error"
                secondary
                :disabled="!canBatchReject"
                :loading="drafts.batchRunning"
              >
                批量拒绝
              </NButton>
            </template>
            将拒绝选中的待审核草稿，确定继续吗？
          </NPopconfirm>
          <NPopconfirm @positive-click="runBatch('discard')">
            <template #trigger>
              <NButton
                size="small"
                secondary
                :disabled="!canBatchDiscard"
                :loading="drafts.batchRunning"
              >
                批量放弃
              </NButton>
            </template>
            将放弃选中的未终态草稿，确定继续吗？
          </NPopconfirm>
        </div>
      </section>

      <section
        v-if="batchResultList.length"
        class="batch-results"
        aria-live="polite"
      >
        <span
          v-for="result in batchResultList"
          :key="`${result.action}:${result.draftId}`"
          class="batch-result"
          :class="`batch-result--${result.kind}`"
        >
          {{ result.draftId.slice(0, 8) }} · {{ batchResultLabel(result) }}
        </span>
      </section>

      <div class="review-grid">
        <section class="draft-list" aria-label="草稿列表">
          <div v-if="drafts.loading && !drafts.items.length" class="loading">
            <NSpin size="small" />
            <span>正在读取草稿...</span>
          </div>
          <NEmpty v-else-if="!drafts.items.length" description="暂无草稿" />
          <template v-else>
            <div
              v-for="item in drafts.items"
              :key="item.draftId"
              class="draft-row"
              :class="{
                'draft-row--active':
                  selected?.metadata.draftId === item.draftId,
              }"
            >
              <NCheckbox
                :checked="drafts.selectedDraftIds.includes(item.draftId)"
                :aria-label="`选择 ${item.title || '未命名草稿'}`"
                @update:checked="toggleDraft(item.draftId)"
              />
              <button
                type="button"
                class="draft-row__main"
                @click="selectDraft(item)"
              >
                <span class="draft-row__title">
                  {{ item.title || "未命名草稿" }}
                </span>
                <span>{{ item.virtualPath || "新文档" }}</span>
                <span class="draft-row__meta">
                  {{ statusLabel(item.status) }} · R{{ item.draftRevision }} ·
                  {{ item.sourceActor }}
                </span>
                <span>
                  {{ item.draftType }} ·
                  {{ new Date(item.updatedAt).toLocaleString() }}
                </span>
              </button>
            </div>
          </template>
          <NButton
            v-if="drafts.nextCursor"
            secondary
            size="small"
            :loading="drafts.loading"
            @click="drafts.load(true)"
          >
            加载更多
          </NButton>
        </section>

        <section class="draft-detail">
          <div v-if="drafts.detailLoading" class="loading">
            <NSpin size="small" />
            <span>正在读取详情...</span>
          </div>
          <NEmpty v-else-if="!selected" description="选择一份草稿查看预览" />
          <template v-else>
            <header class="detail-heading">
              <div>
                <h2>{{ selected.metadata.title || "未命名草稿" }}</h2>
                <p>
                  {{ statusLabel(selected.metadata.status) }} ·
                  {{ selected.metadata.sourceActor }} · R{{
                    selected.metadata.draftRevision
                  }}
                </p>
                <p v-if="selected.metadata.futureDocumentId">
                  正式 ID：{{ selected.metadata.futureDocumentId }}
                </p>
              </div>
              <NTag size="small" :bordered="false">
                {{ selected.metadata.draftType }}
              </NTag>
            </header>

            <NAlert
              v-if="writeModeState === 'direct'"
              type="warning"
              :show-icon="false"
            >
              <template #icon>
                <NIcon :component="ShieldAlert" aria-hidden="true" />
              </template>
              MCP 可能直接修改正式知识，相关变更不一定经过审核队列。
            </NAlert>
            <div v-else class="write-mode-line">
              <NIcon
                :component="
                  writeModeState === 'draft' ? ShieldCheck : ShieldAlert
                "
                aria-hidden="true"
              />
              <span v-if="writeModeState === 'loading'"
                >写入模式状态读取中</span
              >
              <span v-else-if="writeModeState === 'draft'">当前为草稿模式</span>
              <span v-else>写入模式未知</span>
            </div>

            <section v-if="selected.reviewContext" class="context-section">
              <header>
                <h3>来源信息 · 提交方提供，未经核实</h3>
              </header>
              <p v-if="selected.reviewContext.summary" class="context-summary">
                {{ selected.reviewContext.summary }}
              </p>
              <ul
                v-if="selected.reviewContext.sources.length"
                class="source-list"
              >
                <li
                  v-for="(source, index) in selected.reviewContext.sources"
                  :key="`${source.documentId ?? 'source'}:${index}`"
                >
                  <div class="source-heading">
                    <strong>{{ source.title || "未命名来源" }}</strong>
                    <NButton
                      v-if="source.documentId"
                      text
                      type="primary"
                      size="tiny"
                      @click="openSource(source.documentId)"
                    >
                      <template #icon>
                        <NIcon :component="Link2" aria-hidden="true" />
                      </template>
                      打开
                    </NButton>
                  </div>
                  <span v-if="source.virtualPath">{{
                    source.virtualPath
                  }}</span>
                  <blockquote>{{ source.excerpt }}</blockquote>
                  <small v-if="source.reference">{{ source.reference }}</small>
                </li>
              </ul>
            </section>

            <section class="changes-section">
              <header>
                <h3>字段变化</h3>
                <span>
                  {{ selected.preview.structuredChanges.totalChangeCount }} 项
                </span>
              </header>
              <p
                v-if="selected.preview.structuredChanges.truncated"
                class="truncated-note"
              >
                仅显示前 100 项变化。
              </p>
              <ul
                v-if="selected.preview.structuredChanges.changes.length"
                class="change-list"
              >
                <li
                  v-for="(change, index) in selected.preview.structuredChanges
                    .changes"
                  :key="`${change.fieldKind}:${change.key}:${index}`"
                >
                  <div class="change-name">
                    <strong>{{ changeLabel(change) }}</strong>
                    <small
                      v-if="change.beforeValueType || change.afterValueType"
                    >
                      {{ change.beforeValueType || "不存在" }} ->
                      {{ change.afterValueType || "不存在" }}
                    </small>
                  </div>
                  <div class="change-values">
                    <span
                      :class="{ 'change-value--missing': !change.beforeExists }"
                    >
                      {{ changeValue(change, "before") }}
                    </span>
                    <span aria-hidden="true">-></span>
                    <span
                      :class="{ 'change-value--missing': !change.afterExists }"
                    >
                      {{ changeValue(change, "after") }}
                    </span>
                  </div>
                </li>
              </ul>
              <NEmpty v-else description="没有字段变化" size="small" />
            </section>

            <section class="operation-section">
              <h3>操作摘要</h3>
              <div class="operation-tags">
                <NTag
                  v-for="item in selected.preview.operationSummary.items"
                  :key="item.type"
                  size="small"
                  :bordered="false"
                >
                  {{ item.type }} x {{ item.count }}
                </NTag>
                <span v-if="!selected.preview.operationSummary.items.length">
                  无类型化操作
                </span>
              </div>
            </section>

            <template v-if="selected.metadata.draftType === 'create'">
              <label>
                标题
                <NInput v-model:value="title" :disabled="!editable" />
              </label>
              <label>
                路径
                <NInput v-model:value="virtualPath" :disabled="!editable" />
              </label>
              <label>
                格式
                <NSelect
                  v-model:value="format"
                  :disabled="!editable"
                  :options="[
                    { label: 'Markdown', value: 'md' },
                    { label: 'Text', value: 'txt' },
                  ]"
                />
              </label>
              <label>
                标签
                <NInput v-model:value="tags" :disabled="!editable" />
              </label>
            </template>

            <label
              v-if="selected.metadata.draftType !== 'trash'"
              class="content-field"
            >
              正文
              <NInput
                v-model:value="content"
                type="textarea"
                :autosize="{ minRows: 14, maxRows: 28 }"
                :disabled="!contentEditable"
              />
            </label>
            <p
              v-if="
                editable &&
                selected.metadata.draftType === 'update' &&
                !selected.preview.bodyEdit.canSave
              "
              class="edit-warning"
            >
              {{ bodyEditReason(selected.preview.bodyEdit.reason) }}
            </p>
            <p
              v-else-if="editable && selected.metadata.draftType === 'update'"
              class="edit-note"
            >
              保存编辑会归一化正文操作，但保留其他类型的变更。
            </p>

            <details v-if="selected.preview.unifiedDiff" class="diff-section">
              <summary>查看完整 Diff</summary>
              <pre>{{ selected.preview.unifiedDiff }}</pre>
              <p v-if="selected.preview.diffTruncated">Diff 已截断。</p>
            </details>

            <footer v-if="!isTerminal" class="detail-actions">
              <NButton
                v-if="editable"
                :disabled="
                  !contentEditable && selected.metadata.draftType !== 'create'
                "
                :loading="drafts.actionLoading"
                @click="save"
              >
                保存编辑
              </NButton>
              <NButton
                v-if="selected.metadata.status === 'draft'"
                type="primary"
                :loading="drafts.actionLoading"
                @click="submitReview"
              >
                送审
              </NButton>
              <NButton
                v-if="canRebase"
                type="warning"
                :loading="drafts.actionLoading"
                @click="rebase"
              >
                基于最新版本重建
              </NButton>
              <NPopconfirm
                v-if="selected.metadata.status === 'pending_review'"
                @positive-click="reject"
              >
                <template #trigger>
                  <NButton
                    type="error"
                    secondary
                    :loading="drafts.actionLoading"
                  >
                    拒绝
                  </NButton>
                </template>
                拒绝后该草稿进入终态，确定继续吗？
              </NPopconfirm>
              <NPopconfirm v-if="canDiscard" @positive-click="discard">
                <template #trigger>
                  <NButton secondary :loading="drafts.actionLoading">
                    放弃
                  </NButton>
                </template>
                放弃后该草稿进入终态，确定继续吗？
              </NPopconfirm>
              <NPopconfirm v-if="canApply" @positive-click="apply">
                <template #trigger>
                  <NButton type="primary" :loading="drafts.actionLoading">
                    提交正式文档
                  </NButton>
                </template>
                提交后会生成正式修订和审计记录，确定继续吗？
              </NPopconfirm>
            </footer>
          </template>
        </section>
      </div>
    </template>
  </section>
</template>

<style scoped>
.review-view {
  width: min(100%, 1380px);
  padding: 32px 40px 80px;
}

.page-heading {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 20px;
}

.page-heading p {
  margin: 0 0 5px;
  color: var(--cv-muted);
  font-size: 12px;
}

.workspace-switch {
  margin-bottom: 18px;
}

.page-heading h1 {
  margin: 0;
  font-size: 2rem;
  letter-spacing: 0;
}

.summary-strip {
  display: flex;
  gap: 32px;
  margin-bottom: 18px;
  padding: 14px 0;
  border-top: 1px solid var(--cv-border);
  border-bottom: 1px solid var(--cv-border);
}

.summary-strip div {
  display: flex;
  align-items: baseline;
  gap: 8px;
}

.summary-strip span {
  color: var(--cv-muted);
  font-size: 12px;
}

.summary-strip strong {
  font-size: 1.35rem;
  font-variant-numeric: tabular-nums;
}

.queue-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin: 18px 0 12px;
}

.queue-count {
  margin-left: 6px;
  color: var(--cv-muted);
  font-size: 11px;
}

.selection-count {
  color: var(--cv-muted);
  font-size: 12px;
}

.filter-bar {
  display: grid;
  grid-template-columns:
    minmax(120px, 0.8fr)
    minmax(120px, 0.8fr)
    minmax(120px, 0.8fr)
    minmax(160px, 1fr)
    minmax(160px, 1fr);
  gap: 8px;
  margin-bottom: 12px;
}

.batch-bar,
.batch-results {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  margin-bottom: 12px;
  padding: 10px 0;
  border-top: 1px solid var(--cv-border);
  border-bottom: 1px solid var(--cv-border);
}

.batch-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.batch-results {
  justify-content: flex-start;
}

.batch-result {
  padding: 3px 7px;
  border-radius: 4px;
  background: color-mix(in srgb, var(--cv-muted) 10%, transparent);
  color: var(--cv-muted);
  font-size: 11px;
}

.batch-result--success {
  color: var(--cv-accent);
}

.batch-result--revision_conflict,
.batch-result--not_editable,
.batch-result--failed {
  color: var(--cv-danger);
}

.review-grid {
  display: grid;
  grid-template-columns: minmax(280px, 0.34fr) minmax(0, 1fr);
  gap: 24px;
}

.draft-list {
  display: grid;
  align-content: start;
  gap: 4px;
  border-right: 1px solid var(--cv-border);
  padding-right: 20px;
}

.draft-row {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  gap: 8px;
  align-items: start;
  border-radius: 6px;
}

.draft-row:hover,
.draft-row--active {
  background: color-mix(in srgb, var(--cv-accent) 10%, transparent);
}

.draft-row .n-checkbox {
  margin: 13px 0 0 10px;
}

.draft-row__main {
  display: grid;
  gap: 4px;
  min-width: 0;
  padding: 11px 10px 11px 0;
  border: 0;
  background: transparent;
  color: var(--cv-text);
  text-align: left;
}

.draft-row__main span {
  overflow-wrap: anywhere;
  color: var(--cv-muted);
  font-size: 11px;
}

.draft-row--active .draft-row__main span {
  color: color-mix(in srgb, var(--cv-text) 78%, var(--cv-muted));
}

.draft-row__main .draft-row__title {
  color: var(--cv-text);
  font-size: 13px;
  font-weight: 650;
}

.draft-row__meta {
  color: var(--cv-accent) !important;
}

.draft-detail {
  display: grid;
  min-width: 0;
  align-content: start;
  gap: 14px;
}

.detail-heading {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.detail-heading h2,
.context-section h3,
.changes-section h3,
.operation-section h3,
.diff-section summary {
  letter-spacing: 0;
}

.detail-heading h2 {
  margin: 0;
  font-size: 1.25rem;
}

.detail-heading p,
.truncated-note,
.edit-note,
.edit-warning,
.diff-section p {
  margin: 4px 0 0;
  color: var(--cv-muted);
  font-size: 11px;
}

.edit-warning {
  color: var(--cv-danger);
}

.write-mode-line {
  display: flex;
  align-items: center;
  gap: 7px;
  color: var(--cv-muted);
  font-size: 12px;
}

.context-section,
.changes-section,
.operation-section {
  display: grid;
  gap: 9px;
  padding: 13px 0;
  border-top: 1px solid var(--cv-border);
}

.context-section header,
.changes-section header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.context-section h3,
.changes-section h3,
.operation-section h3 {
  margin: 0;
  font-size: 13px;
}

.context-summary {
  margin: 0;
  white-space: pre-wrap;
}

.source-list,
.change-list {
  display: grid;
  gap: 9px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.source-list li {
  display: grid;
  gap: 4px;
  padding-left: 10px;
  border-left: 2px solid var(--cv-border);
}

.source-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.source-list span,
.source-list small {
  color: var(--cv-muted);
  font-size: 11px;
}

.source-list blockquote {
  margin: 0;
  padding: 8px 10px;
  border-left: 2px solid var(--cv-accent);
  background: color-mix(in srgb, var(--cv-accent) 6%, transparent);
  white-space: pre-wrap;
}

.changes-section > span,
.changes-section header span {
  color: var(--cv-muted);
  font-size: 11px;
}

.change-list li {
  display: grid;
  grid-template-columns: minmax(140px, 0.34fr) minmax(0, 1fr);
  gap: 12px;
  padding: 8px 0;
  border-bottom: 1px solid color-mix(in srgb, var(--cv-border) 65%, transparent);
}

.change-name {
  display: grid;
  align-content: start;
  gap: 2px;
}

.change-name small {
  color: var(--cv-muted);
}

.change-values {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr);
  gap: 8px;
  align-items: start;
}

.change-values span {
  overflow-wrap: anywhere;
  font-family: "Cascadia Mono", "SFMono-Regular", Consolas, monospace;
  font-size: 11px;
  white-space: pre-wrap;
}

.change-values .change-value--missing {
  color: var(--cv-muted);
  font-style: italic;
}

.operation-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  color: var(--cv-muted);
  font-size: 12px;
}

.draft-detail label {
  display: grid;
  gap: 6px;
  color: var(--cv-muted);
  font-size: 12px;
}

.content-field {
  min-width: 0;
}

.diff-section {
  min-width: 0;
}

.diff-section summary {
  cursor: pointer;
  color: var(--cv-accent);
  font-size: 12px;
  font-weight: 650;
}

.diff-section pre {
  max-height: 320px;
  margin: 8px 0 0;
  overflow: auto;
  padding: 14px;
  border: 1px solid var(--cv-border);
  border-radius: 6px;
  background: var(--cv-bg);
  font-size: 12px;
  white-space: pre-wrap;
}

.detail-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.loading {
  display: flex;
  align-items: center;
  gap: 10px;
  color: var(--cv-muted);
}

@media (max-width: 1099px) {
  .filter-bar {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }
}

@media (max-width: 899px) {
  .review-view {
    padding: 24px 20px 64px;
  }

  .summary-strip {
    justify-content: space-between;
    gap: 12px;
  }

  .queue-bar {
    align-items: flex-start;
    flex-direction: column;
  }

  .filter-bar {
    grid-template-columns: 1fr 1fr;
  }

  .review-grid {
    grid-template-columns: 1fr;
  }

  .draft-list {
    border-right: 0;
    border-bottom: 1px solid var(--cv-border);
    padding: 0 0 16px;
  }

  .change-list li {
    grid-template-columns: 1fr;
    gap: 6px;
  }
}

@media (max-width: 599px) {
  .filter-bar {
    grid-template-columns: 1fr;
  }

  .summary-strip strong {
    font-size: 1.1rem;
  }

  .change-values {
    grid-template-columns: 1fr;
  }
}
</style>
