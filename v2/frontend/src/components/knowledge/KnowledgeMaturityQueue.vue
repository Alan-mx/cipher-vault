<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import {
  NAlert,
  NButton,
  NEmpty,
  NIcon,
  NModal,
  NPopconfirm,
  NRadioButton,
  NRadioGroup,
  NSelect,
  NSpin,
  NTag,
  NTooltip,
  useMessage,
} from "naive-ui";
import { ArrowRight, Check, RefreshCw, Settings2, Trash2 } from "@lucide/vue";
import { useKnowledgeMaturityQueueStore } from "../../stores/knowledge-maturity-queue";
import type {
  KnowledgeMaturityLevel,
  KnowledgeMaturityQueueBucket,
  KnowledgeMaturityQueueItem,
} from "../../types/api";

const props = defineProps<{
  bucket: KnowledgeMaturityQueueBucket;
}>();

const emit = defineEmits<{
  changeBucket: [bucket: KnowledgeMaturityQueueBucket];
}>();

const queue = useKnowledgeMaturityQueueStore();
const message = useMessage();
const editOpen = ref(false);
const editItem = ref<KnowledgeMaturityQueueItem | null>(null);
const editLevel = ref<KnowledgeMaturityLevel | null>(null);

const bucketOptions: Array<{
  value: KnowledgeMaturityQueueBucket;
  label: string;
}> = [
  { value: "pending_organization", label: "待整理" },
  { value: "pending_verification", label: "待验证" },
  { value: "completed", label: "已完成" },
  { value: "other", label: "其他值" },
];

const levelOptions: Array<{
  value: KnowledgeMaturityLevel;
  label: string;
}> = [
  { value: "raw", label: "raw" },
  { value: "excerpt", label: "excerpt" },
  { value: "organized", label: "organized" },
  { value: "verified", label: "verified" },
  { value: "practiced", label: "practiced" },
];

const levelLabels: Record<KnowledgeMaturityLevel, string> = {
  raw: "原始",
  excerpt: "摘录",
  organized: "已整理",
  verified: "已验证",
  practiced: "已实践",
};

const typeLabels = {
  missing: "未设置",
  null: "null",
  string: "字符串",
  number: "数字",
  boolean: "布尔值",
  array: "数组",
  object: "对象",
} as const;

const editBefore = computed(() =>
  editItem.value ? maturityDisplay(editItem.value) : "",
);
const editAfter = computed(() =>
  editLevel.value ? levelLabels[editLevel.value] : "未设置",
);

const load = async (): Promise<void> => {
  if (queue.bucket !== props.bucket || !queue.items.length) {
    await queue.load(props.bucket);
  }
};

const refresh = async (): Promise<void> => {
  await queue.refresh();
};

const changeBucket = (value: string | number): void => {
  emit("changeBucket", value as KnowledgeMaturityQueueBucket);
};

const openEdit = (item: KnowledgeMaturityQueueItem): void => {
  editItem.value = item;
  editLevel.value = item.nextMaturity ?? standardValue(item);
  editOpen.value = true;
};

const saveEdit = async (): Promise<void> => {
  const item = editItem.value;
  const level = editLevel.value;
  if (!item || !level) {
    return;
  }
  if (await queue.setLevel(item, level)) {
    editOpen.value = false;
    message.success("知识成熟度已更新。");
  }
};

const advance = async (item: KnowledgeMaturityQueueItem): Promise<void> => {
  if (await queue.advance(item)) {
    message.success("知识成熟度已更新。");
  } else {
    message.error(queue.actionError?.message ?? "更新知识成熟度失败。");
  }
};

const clear = async (item: KnowledgeMaturityQueueItem): Promise<void> => {
  if (await queue.clear(item)) {
    message.success("知识成熟度已更新。");
  } else {
    message.error(queue.actionError?.message ?? "清除知识成熟度失败。");
  }
};

const maturityDisplay = (item: KnowledgeMaturityQueueItem): string => {
  if (item.maturityValueType !== "string") {
    return typeLabels[item.maturityValueType];
  }
  return item.maturityValue === "" ? "空字符串" : (item.maturityValue ?? "");
};

const standardValue = (
  item: KnowledgeMaturityQueueItem,
): KnowledgeMaturityLevel | null => {
  if (item.maturityValueType !== "string" || !item.maturityValue) {
    return null;
  }
  const normalized = item.maturityValue.normalize("NFC").trim().toLowerCase();
  return levelOptions.some((option) => option.value === normalized)
    ? (normalized as KnowledgeMaturityLevel)
    : null;
};

const canEdit = (item: KnowledgeMaturityQueueItem): boolean =>
  item.format === "md";

const canAdvance = (item: KnowledgeMaturityQueueItem): boolean =>
  canEdit(item) && item.nextMaturity !== null;

const advanceLabel = (item: KnowledgeMaturityQueueItem): string =>
  item.maturityValueType === "string" && item.maturityValue
    ? `前进到 ${levelLabels[item.nextMaturity!]}`
    : `初始化为 ${levelLabels[item.nextMaturity!]}`;

const canClear = (item: KnowledgeMaturityQueueItem): boolean =>
  canEdit(item) && item.maturityValueType !== "missing";

const rowBusy = (item: KnowledgeMaturityQueueItem): boolean =>
  queue.actionDocumentId === item.documentId;

watch(
  () => props.bucket,
  () => void load(),
);
onMounted(() => void load());
</script>

<template>
  <section class="maturity-queue" aria-label="知识成熟度整理队列">
    <div class="queue-toolbar">
      <NRadioGroup
        :value="props.bucket"
        size="small"
        aria-label="成熟度整理分组"
        @update:value="changeBucket"
      >
        <NRadioButton
          v-for="option in bucketOptions"
          :key="option.value"
          :value="option.value"
        >
          {{ option.label }}
        </NRadioButton>
      </NRadioGroup>
      <NTooltip trigger="hover">
        <template #trigger>
          <NButton
            quaternary
            circle
            aria-label="刷新整理队列"
            :loading="queue.loading || queue.loadingMore"
            @click="refresh"
          >
            <template #icon>
              <NIcon :component="RefreshCw" aria-hidden="true" />
            </template>
          </NButton>
        </template>
        刷新整理队列
      </NTooltip>
    </div>

    <NAlert v-if="queue.error" type="error" :show-icon="false">
      <p class="inline-error">{{ queue.error.message }}</p>
      <NButton size="tiny" secondary @click="refresh">重试</NButton>
    </NAlert>
    <NAlert v-if="queue.actionError" type="error" :show-icon="false">
      {{ queue.actionError.message }}
    </NAlert>

    <div v-if="queue.loading && !queue.items.length" class="queue-loading">
      <NSpin size="small" />
      <span>正在读取整理队列…</span>
    </div>
    <NEmpty v-else-if="!queue.items.length" description="当前分组没有文档" />
    <div v-else class="queue-list">
      <article
        v-for="item in queue.items"
        :key="item.documentId"
        class="queue-item"
      >
        <header>
          <div>
            <strong>{{ item.title }}</strong>
            <p>{{ item.virtualPath }}</p>
          </div>
          <NTag size="small" :bordered="false">
            {{ typeLabels[item.maturityValueType] }}
          </NTag>
        </header>

        <div class="maturity-readout">
          <span>当前</span>
          <strong>{{ maturityDisplay(item) }}</strong>
          <template v-if="item.nextMaturity">
            <NIcon :component="ArrowRight" aria-hidden="true" />
            <span>下一步</span>
            <strong>{{ levelLabels[item.nextMaturity] }}</strong>
          </template>
        </div>

        <div class="queue-meta">
          <span>{{ item.format.toUpperCase() }}</span>
          <span>R{{ item.revision }}</span>
          <span>{{ new Date(item.updatedAt).toLocaleString("zh-CN") }}</span>
          <span v-for="tag in item.tags" :key="tag">#{{ tag }}</span>
        </div>

        <p v-if="item.format === 'txt'" class="read-only-note">
          TXT 不支持 Frontmatter 成熟度
        </p>
        <footer>
          <NPopconfirm v-if="canAdvance(item)" @positive-click="advance(item)">
            <template #trigger>
              <NButton size="small" type="primary" :loading="rowBusy(item)">
                <template #icon>
                  <NIcon :component="Check" aria-hidden="true" />
                </template>
                {{ advanceLabel(item) }}
              </NButton>
            </template>
            将成熟度从“{{ maturityDisplay(item) }}”改为“{{
              levelLabels[item.nextMaturity!]
            }}”，确认继续吗？
          </NPopconfirm>
          <NButton
            size="small"
            secondary
            :disabled="!canEdit(item)"
            :loading="rowBusy(item)"
            @click="openEdit(item)"
          >
            <template #icon>
              <NIcon :component="Settings2" aria-hidden="true" />
            </template>
            设置成熟度
          </NButton>
          <NPopconfirm v-if="canClear(item)" @positive-click="clear(item)">
            <template #trigger>
              <NButton size="small" quaternary :loading="rowBusy(item)">
                <template #icon>
                  <NIcon :component="Trash2" aria-hidden="true" />
                </template>
                清除
              </NButton>
            </template>
            将成熟度从“{{ maturityDisplay(item) }}”改为“未设置”，并从
            Frontmatter 删除顶层 maturity Key，确认继续吗？
          </NPopconfirm>
        </footer>
      </article>
    </div>

    <div v-if="queue.nextCursor" class="load-more">
      <NButton
        secondary
        :loading="queue.loadingMore"
        @click="queue.load(queue.bucket, true)"
      >
        加载更多
      </NButton>
    </div>

    <NModal
      v-model:show="editOpen"
      preset="card"
      title="设置知识成熟度"
      class="maturity-edit-modal"
    >
      <div class="edit-comparison">
        <div>
          <span>修改前</span>
          <strong>{{ editBefore }}</strong>
        </div>
        <NIcon :component="ArrowRight" aria-hidden="true" />
        <div>
          <span>修改后</span>
          <strong>{{ editAfter }}</strong>
        </div>
      </div>
      <NSelect
        v-model:value="editLevel"
        :options="levelOptions"
        aria-label="目标成熟度"
        placeholder="选择标准成熟度"
      />
      <template #footer>
        <div class="modal-actions">
          <NButton @click="editOpen = false">取消</NButton>
          <NButton
            type="primary"
            :loading="
              editItem !== null &&
              queue.actionDocumentId === editItem.documentId
            "
            :disabled="editLevel === null"
            @click="saveEdit"
          >
            确认修改
          </NButton>
        </div>
      </template>
    </NModal>
  </section>
</template>

<style scoped>
.maturity-queue {
  display: grid;
  gap: 12px;
}

.queue-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.queue-toolbar .n-button {
  flex: 0 0 auto;
}

.queue-list {
  display: grid;
  gap: 8px;
}

.queue-item {
  display: grid;
  gap: 9px;
  padding: 13px 14px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
}

.queue-item > header {
  display: flex;
  align-items: start;
  justify-content: space-between;
  gap: 12px;
}

.queue-item > header strong {
  font-size: 14px;
}

.queue-item > header p,
.queue-meta,
.read-only-note {
  margin: 0;
  color: var(--cv-muted);
  font-size: 11px;
}

.queue-item > header p {
  margin-top: 3px;
  overflow-wrap: anywhere;
}

.maturity-readout {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  font-size: 12px;
}

.maturity-readout span {
  color: var(--cv-muted);
}

.maturity-readout strong {
  font-weight: 650;
}

.queue-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 12px;
}

.queue-item footer {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.read-only-note {
  color: var(--cv-text);
  font-weight: 600;
}

.queue-loading {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 32px 0;
  color: var(--cv-muted);
}

.load-more {
  display: flex;
  justify-content: center;
  padding-top: 4px;
}

.inline-error {
  margin: 0 0 8px;
}

.edit-comparison {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr);
  align-items: center;
  gap: 12px;
  margin-bottom: 16px;
}

.edit-comparison > div {
  display: grid;
  gap: 4px;
  padding: 10px;
  border: 1px solid var(--cv-border);
  border-radius: 6px;
}

.edit-comparison span {
  color: var(--cv-muted);
  font-size: 11px;
}

.edit-comparison strong {
  overflow-wrap: anywhere;
}

.modal-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}

@media (max-width: 599px) {
  .queue-toolbar {
    align-items: stretch;
    flex-direction: column;
  }

  .queue-toolbar .n-button {
    align-self: flex-end;
  }

  .edit-comparison {
    grid-template-columns: 1fr;
  }

  .edit-comparison > .n-icon {
    justify-self: center;
    transform: rotate(90deg);
  }
}
</style>
