<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import {
  NAlert,
  NButton,
  NEmpty,
  NInput,
  NPopconfirm,
  NSelect,
  NSpin,
  NTag,
  useMessage,
} from "naive-ui";
import { useDocumentDraftsStore } from "../stores/drafts";
import { documentApi } from "../api/documents";
import { registerSensitiveStore } from "../session/lifecycle";
import type { DocumentDraftItem, DocumentDraftStatus } from "../types/api";
import type { VaultFormat } from "../types/domain";

const drafts = useDocumentDraftsStore();
const message = useMessage();
const title = ref("");
const virtualPath = ref("");
const format = ref<VaultFormat>("md");
const content = ref("");
const tags = ref("");
let unregisterSensitiveStore: (() => void) | null = null;

const statusOptions = [
  { label: "待审核", value: "pending_review" },
  { label: "草稿", value: "draft" },
  { label: "冲突", value: "conflict" },
] as const;

const selected = computed(() => drafts.selected);
const editable = computed(
  () =>
    selected.value?.metadata.status === "draft" ||
    selected.value?.metadata.status === "pending_review",
);
const canRebase = computed(
  () => selected.value?.metadata.status === "conflict",
);

watch(
  () => drafts.selected,
  (detail) => {
    if (!detail) {
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
  unregisterSensitiveStore = registerSensitiveStore(drafts);
  void drafts.load();
});

onBeforeUnmount(() => {
  unregisterSensitiveStore?.();
  unregisterSensitiveStore = null;
  drafts.resetSensitive();
});

const selectDraft = async (item: DocumentDraftItem): Promise<void> => {
  await drafts.select(item.draftId);
};

const changeStatus = async (value: DocumentDraftStatus): Promise<void> => {
  drafts.selected = null;
  await drafts.load(false, value);
};

const parseTags = (): string[] =>
  tags.value
    .split(",")
    .map((tag) => tag.trim())
    .filter(Boolean);

const save = async (): Promise<void> => {
  if (!selected.value) {
    return;
  }
  const payload =
    selected.value.metadata.draftType === "create"
      ? {
          title: title.value.trim(),
          virtualPath: virtualPath.value.trim() || null,
          format: format.value,
          content: content.value,
          tags: parseTags(),
        }
      : {
          operations: [
            {
              type: "replace_body",
              content: content.value,
            },
          ],
        };
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
</script>

<template>
  <section class="review-view">
    <header class="page-heading">
      <div>
        <p>AI 草稿</p>
        <h1>待审核</h1>
      </div>
      <NSelect
        class="status-filter"
        :value="drafts.statusFilter"
        :options="[...statusOptions]"
        @update:value="changeStatus"
      />
    </header>

    <NAlert v-if="drafts.error" type="error" :show-icon="false">
      {{ drafts.error.message }}
    </NAlert>

    <div class="review-grid">
      <section class="draft-list" aria-label="草稿列表">
        <div v-if="drafts.loading && !drafts.items.length" class="loading">
          <NSpin size="small" />
          <span>正在读取草稿…</span>
        </div>
        <NEmpty v-else-if="!drafts.items.length" description="暂无草稿" />
        <button
          v-for="item in drafts.items"
          v-else
          :key="item.draftId"
          type="button"
          class="draft-row"
          :class="{
            'draft-row--active': selected?.metadata.draftId === item.draftId,
          }"
          @click="selectDraft(item)"
        >
          <span class="draft-row__title">
            {{ item.title || "未命名草稿" }}
          </span>
          <span>{{ item.virtualPath || "新文档" }}</span>
          <span>R{{ item.draftRevision }} · {{ item.draftType }}</span>
          <span v-if="item.futureDocumentId">
            ID {{ item.futureDocumentId.slice(0, 8) }}
          </span>
        </button>
        <NButton
          v-if="drafts.nextCursor"
          secondary
          size="small"
          @click="drafts.load(true)"
        >
          加载更多
        </NButton>
      </section>

      <section class="draft-detail">
        <NEmpty v-if="!selected" description="选择一份草稿查看预览" />
        <template v-else>
          <header class="detail-heading">
            <div>
              <h2>{{ selected.metadata.title || "未命名草稿" }}</h2>
              <p>
                {{ selected.metadata.sourceActor }} ·
                {{ selected.metadata.status }} · R{{
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

          <label class="content-field">
            正文
            <NInput
              v-model:value="content"
              type="textarea"
              :autosize="{ minRows: 14, maxRows: 28 }"
              :disabled="!editable"
            />
          </label>

          <section class="diff-section">
            <h3>变更预览</h3>
            <pre>{{ selected.preview.unifiedDiff || "无正文差异" }}</pre>
            <p v-if="selected.preview.diffTruncated">Diff 已截断。</p>
          </section>

          <footer class="detail-actions">
            <NButton
              v-if="editable"
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
                <NButton type="error" secondary :loading="drafts.actionLoading">
                  拒绝
                </NButton>
              </template>
              拒绝后该草稿进入终态，确定继续吗？
            </NPopconfirm>
            <NPopconfirm
              v-if="selected.metadata.status !== 'applied'"
              @positive-click="discard"
            >
              <template #trigger>
                <NButton secondary :loading="drafts.actionLoading">
                  放弃
                </NButton>
              </template>
              放弃后该草稿进入终态，确定继续吗？
            </NPopconfirm>
            <NPopconfirm
              v-if="selected.metadata.status === 'pending_review'"
              @positive-click="apply"
            >
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
  </section>
</template>

<style scoped>
.review-view {
  width: min(100%, 1260px);
  padding: 32px 40px 80px;
}

.page-heading {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 24px;
}

.page-heading p {
  margin: 0 0 5px;
  color: var(--cv-muted);
  font-size: 12px;
}

.page-heading h1 {
  margin: 0;
  font-size: 2rem;
  letter-spacing: 0;
}

.status-filter {
  width: 150px;
}

.review-grid {
  display: grid;
  grid-template-columns: minmax(230px, 0.32fr) minmax(0, 1fr);
  gap: 20px;
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
  gap: 4px;
  padding: 12px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--cv-text);
  text-align: left;
}

.draft-row:hover,
.draft-row--active {
  background: color-mix(in srgb, var(--cv-accent) 10%, transparent);
}

.draft-row span {
  color: var(--cv-muted);
  font-size: 11px;
}

.draft-row .draft-row__title {
  color: var(--cv-text);
  font-size: 13px;
  font-weight: 650;
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

.detail-heading h2 {
  margin: 0;
  font-size: 1.2rem;
}

.detail-heading p,
.diff-section p {
  margin: 4px 0 0;
  color: var(--cv-muted);
  font-size: 11px;
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

.diff-section h3 {
  margin: 0 0 8px;
  font-size: 13px;
}

.diff-section pre {
  max-height: 320px;
  margin: 0;
  overflow: auto;
  padding: 14px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
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

@media (max-width: 899px) {
  .review-view {
    padding: 24px 20px 64px;
  }

  .review-grid {
    grid-template-columns: 1fr;
  }

  .draft-list {
    border-right: 0;
    border-bottom: 1px solid var(--cv-border);
    padding: 0 0 16px;
  }
}
</style>
