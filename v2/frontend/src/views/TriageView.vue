<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { RouterLink, useRoute, useRouter } from "vue-router";
import { knowledgeBaseApi } from "../api/knowledge-base";
import type {
  KnowledgeModel,
  KnowledgeTriageCounts,
  KnowledgeTriageItem,
  KnowledgeTriageReason,
} from "../types/knowledge-base";

const staleOptions = [7, 30, 90] as const;
const reasons = [
  {
    value: "missing_required_property",
    label: "缺少必填属性",
    countKey: "missingRequiredProperty",
  },
  {
    value: "missing_source",
    label: "缺少来源",
    countKey: "missingSource",
  },
  {
    value: "stale",
    label: "长期未更新",
    countKey: "stale",
  },
] as const;

const route = useRoute();
const router = useRouter();
const model = ref<KnowledgeModel | null>(null);
const items = ref<KnowledgeTriageItem[]>([]);
const counts = ref<KnowledgeTriageCounts | null>(null);
const nextCursor = ref<string | null>(null);
const hasMore = ref(false);
const loading = ref(false);
const error = ref<string | null>(null);
const reason = ref<KnowledgeTriageReason | "">(parseReason(route.query.reason));
const knowledgeTypeId = ref(
  typeof route.query.knowledgeTypeId === "string"
    ? route.query.knowledgeTypeId
    : "",
);
const staleDays = ref(parseStaleDays(route.query.staleDays));
const search = ref(
  typeof route.query.search === "string" ? route.query.search : "",
);
const copyItem = ref<KnowledgeTriageItem | null>(null);
const copyText = ref("");
const copyFallback = ref(false);
const copyStatus = ref<"idle" | "copied" | "manual">("idle");

const typeOptions = computed(() =>
  (model.value?.knowledgeTypes ?? [])
    .slice()
    .sort(
      (left, right) =>
        left.sortOrder - right.sortOrder || left.key.localeCompare(right.key),
    ),
);

const load = async (reset = true) => {
  if (reset) {
    items.value = [];
    nextCursor.value = null;
  }
  loading.value = true;
  error.value = null;
  try {
    const page = await knowledgeBaseApi.triage({
      ...(reason.value ? { reason: reason.value } : {}),
      ...(knowledgeTypeId.value
        ? { knowledgeTypeId: knowledgeTypeId.value }
        : {}),
      staleDays: staleDays.value,
      ...(search.value.trim() ? { search: search.value.trim() } : {}),
      ...(reset ? {} : { cursor: nextCursor.value ?? undefined }),
      pageSize: 20,
    });
    items.value = reset ? page.items : [...items.value, ...page.items];
    counts.value = page.counts;
    nextCursor.value = page.nextCursor;
    hasMore.value = page.hasMore;
    await syncQuery();
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "加载失败";
  } finally {
    loading.value = false;
  }
};

const loadMore = async () => {
  if (!hasMore.value || loading.value || !nextCursor.value) {
    return;
  }
  await load(false);
};

const applyFilters = () => load(true);

const selectReason = (value: KnowledgeTriageReason | "") => {
  reason.value = value;
  void load(true);
};

const syncQuery = async () => {
  await router.replace({
    query: {
      ...(reason.value ? { reason: reason.value } : {}),
      ...(knowledgeTypeId.value
        ? { knowledgeTypeId: knowledgeTypeId.value }
        : {}),
      ...(staleDays.value !== 30 ? { staleDays: String(staleDays.value) } : {}),
      ...(search.value.trim() ? { search: search.value.trim() } : {}),
    },
  });
};

const copyTask = async (item: KnowledgeTriageItem) => {
  copyItem.value = item;
  copyText.value = buildTask(item);
  copyFallback.value = false;
  copyStatus.value = "idle";
  if (!navigator.clipboard?.writeText) {
    copyFallback.value = true;
    copyStatus.value = "manual";
    return;
  }
  try {
    await navigator.clipboard.writeText(copyText.value);
    copyStatus.value = "copied";
  } catch {
    copyFallback.value = true;
    copyStatus.value = "manual";
  }
};

const buildTask = (item: KnowledgeTriageItem): string => {
  const reasonLabels = item.reasonCodes.map(reasonLabel).join("、");
  return `请使用 CipherVault MCP Prompt \`review_knowledge_quality\` 检查并改进以下 Knowledge。

Knowledge ID：${item.knowledgeId}
标题：${item.title}
当前 Revision：${item.revision}
整理原因：${reasonLabels}
缺失属性：${item.missingPropertyKeys.length ? item.missingPropertyKeys.join("、") : "无"}

要求：
- 读取当前 Knowledge、属性和 SourceReference。
- 检查来源缺口、结构完整性、重复与证据质量。
- 提出具体改进建议。
- 如需修改，通过 MCP Draft-only 流程创建或更新 Draft。
- 不得直接写入正式 Knowledge，不得 apply 或 reject Draft。`;
};

const reasonLabel = (value: KnowledgeTriageReason): string =>
  reasons.find((item) => item.value === value)?.label ?? value;

const countFor = (key: keyof KnowledgeTriageCounts): number =>
  counts.value?.[key] ?? 0;

const formatDate = (value: string): string =>
  new Intl.DateTimeFormat("zh-CN", {
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).format(new Date(value));

const closeCopy = () => {
  copyItem.value = null;
  copyFallback.value = false;
  copyStatus.value = "idle";
};

onMounted(async () => {
  try {
    model.value = await knowledgeBaseApi.knowledgeModel();
  } catch {
    model.value = null;
  }
  await load(true);
});

function parseReason(value: unknown): KnowledgeTriageReason | "" {
  return value === "missing_source" ||
    value === "missing_required_property" ||
    value === "stale"
    ? value
    : "";
}

function parseStaleDays(value: unknown): number {
  const parsed = Number(value);
  return staleOptions.includes(parsed as (typeof staleOptions)[number])
    ? parsed
    : 30;
}
</script>

<template>
  <section class="page">
    <header class="page-header">
      <div>
        <p class="eyebrow">Triage</p>
        <h1>知识整理</h1>
        <p class="muted">根据当前知识模型和来源情况发现需要关注的知识。</p>
      </div>
      <button
        type="button"
        class="refresh"
        :disabled="loading"
        @click="load(true)"
      >
        {{ loading ? "刷新中" : "刷新" }}
      </button>
    </header>

    <div class="count-grid">
      <button
        type="button"
        class="count-card"
        :class="{ active: reason === '' }"
        @click="selectReason('')"
      >
        <span>待处理</span>
        <strong>{{ countFor("totalDistinct") }}</strong>
      </button>
      <button
        v-for="item in reasons"
        :key="item.value"
        type="button"
        class="count-card"
        :class="{ active: reason === item.value }"
        @click="selectReason(item.value)"
      >
        <span>{{ item.label }}</span>
        <strong>{{ countFor(item.countKey) }}</strong>
      </button>
    </div>

    <section class="draft-backlog">
      <div>
        <strong>Draft 待处理</strong>
        <span>
          {{ countFor("draft") }} 处理中 · {{ countFor("conflict") }} 冲突
        </span>
      </div>
      <RouterLink to="/review">前往 AI 工作区</RouterLink>
    </section>

    <form class="filters" @submit.prevent="applyFilters">
      <label>
        <span>未更新阈值</span>
        <select v-model.number="staleDays" @change="applyFilters">
          <option :value="7">7 天</option>
          <option :value="30">30 天</option>
          <option :value="90">90 天</option>
        </select>
      </label>
      <label>
        <span>知识类型</span>
        <select v-model="knowledgeTypeId" @change="applyFilters">
          <option value="">全部类型</option>
          <option
            v-for="type in typeOptions"
            :key="type.typeId ?? type.key"
            :value="type.typeId ?? ''"
          >
            {{ type.name }}
          </option>
        </select>
      </label>
      <label class="search-field">
        <span>搜索</span>
        <input
          v-model="search"
          placeholder="搜索标题、标签或正文"
          maxlength="128"
        />
      </label>
      <button type="submit" :disabled="loading">应用</button>
    </form>

    <p v-if="error" class="error" role="alert">{{ error }}</p>

    <div class="queue">
      <article v-for="item in items" :key="item.knowledgeId" class="queue-item">
        <header>
          <div>
            <h2>{{ item.title }}</h2>
            <p>
              {{ item.typeName }} · revision {{ item.revision }} · 更新于
              {{ formatDate(item.updatedAt) }}
            </p>
          </div>
          <RouterLink :to="`/knowledge/${item.knowledgeId}`">
            查看详情
          </RouterLink>
        </header>
        <div class="reason-list">
          <span v-for="code in item.reasonCodes" :key="code">
            {{ reasonLabel(code) }}
          </span>
        </div>
        <p v-if="item.missingPropertyKeys.length" class="missing">
          缺少属性：{{ item.missingPropertyKeys.join("、") }}
        </p>
        <p class="meta">
          来源引用 {{ item.sourceReferenceCount }} 条
          <template v-if="item.tags.length">
            · {{ item.tags.join("、") }}
          </template>
        </p>
        <button type="button" class="secondary" @click="copyTask(item)">
          复制 AI 整理任务
        </button>
      </article>
      <div v-if="!loading && items.length === 0" class="empty">
        <strong>当前没有待整理知识。</strong>
        <span>知识缺口会随着模型和来源变化自动更新。</span>
      </div>
      <button
        v-if="hasMore"
        type="button"
        class="load-more"
        :disabled="loading"
        @click="loadMore"
      >
        {{ loading ? "加载中" : "加载更多" }}
      </button>
    </div>

    <div
      v-if="copyItem"
      class="copy-panel"
      role="region"
      aria-label="AI 整理任务"
    >
      <header>
        <strong>AI 整理任务</strong>
        <button
          type="button"
          class="icon-button"
          aria-label="关闭"
          @click="closeCopy"
        >
          ×
        </button>
      </header>
      <p v-if="copyStatus === 'copied'" class="success">已复制。</p>
      <p v-if="copyStatus === 'manual'" class="error">
        无法自动复制，请手动选择下面文本。
      </p>
      <textarea
        readonly
        :value="copyText"
        :class="{ hidden: !copyFallback }"
        aria-label="AI 整理任务文本"
      />
    </div>
  </section>
</template>

<style scoped>
.page {
  padding: 28px;
}
.page-header,
.queue-item header,
.draft-backlog,
.copy-panel header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}
.eyebrow {
  margin: 0 0 6px;
  color: var(--cv-accent);
  font-size: 11px;
  text-transform: uppercase;
}
h1 {
  margin: 0;
  font-size: 28px;
}
h2 {
  margin: 0;
  font-size: 16px;
}
.muted,
.queue-item p,
.draft-backlog span {
  color: var(--cv-muted);
}
.refresh,
.filters button,
.queue-item > button,
.copy-panel button {
  min-height: 38px;
  padding: 8px 13px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-accent);
  color: white;
  cursor: pointer;
}
.refresh:disabled,
.filters button:disabled,
.queue-item > button:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}
.count-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 10px;
  margin: 22px 0;
}
.count-card {
  display: grid;
  gap: 5px;
  padding: 14px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  color: inherit;
  cursor: pointer;
  text-align: left;
}
.count-card.active {
  border-color: var(--cv-accent);
}
.count-card span {
  color: var(--cv-muted);
  font-size: 12px;
}
.count-card strong {
  font-size: 25px;
}
.draft-backlog {
  margin-bottom: 14px;
  padding: 12px 14px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}
.draft-backlog div {
  display: grid;
  gap: 3px;
}
.draft-backlog a,
.queue-item a {
  color: inherit;
}
.filters {
  display: grid;
  grid-template-columns: 150px 220px minmax(0, 1fr) auto;
  gap: 10px;
  align-items: end;
  margin-bottom: 16px;
}
.filters label {
  display: grid;
  gap: 5px;
  color: var(--cv-muted);
  font-size: 12px;
}
.filters input,
.filters select {
  min-height: 40px;
  padding: 7px 10px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-surface);
  color: var(--cv-text);
}
.queue {
  display: grid;
  gap: 10px;
}
.queue-item,
.empty {
  padding: 14px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}
.queue-item h2 {
  overflow-wrap: anywhere;
}
.queue-item header p {
  margin: 4px 0 0;
  font-size: 12px;
}
.queue-item .secondary {
  border: 1px solid var(--cv-border);
  background: transparent;
  color: var(--cv-text);
}
.reason-list {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 12px;
}
.reason-list span {
  padding: 4px 8px;
  border: 1px solid var(--cv-border);
  border-radius: 999px;
  background: var(--cv-bg);
  font-size: 12px;
}
.queue-item .missing {
  color: #b93f36;
}
.queue-item > button {
  margin-top: 10px;
}
.meta {
  font-size: 12px;
}
.empty {
  display: grid;
  gap: 5px;
  color: var(--cv-muted);
}
.empty strong {
  color: var(--cv-text);
}
.load-more {
  justify-self: start;
  padding: 8px 13px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-surface);
  color: var(--cv-text);
  cursor: pointer;
}
.error {
  color: #b93f36;
}
.success {
  color: #2e7d4f;
}
.copy-panel {
  position: fixed;
  right: 24px;
  bottom: 24px;
  z-index: 20;
  width: min(680px, calc(100vw - 48px));
  padding: 14px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  box-shadow: 0 12px 40px rgb(0 0 0 / 20%);
}
.copy-panel textarea {
  width: 100%;
  min-height: 220px;
  margin-top: 10px;
  padding: 10px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-bg);
  color: var(--cv-text);
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  font-size: 12px;
}
.copy-panel textarea.hidden {
  display: none;
}
.icon-button {
  width: 34px;
  min-height: 34px;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--cv-text);
  font-size: 20px;
}
@media (max-width: 800px) {
  .page {
    padding: 18px;
  }
  .page-header,
  .queue-item header {
    align-items: stretch;
    flex-direction: column;
  }
  .count-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
  .filters {
    grid-template-columns: 1fr;
  }
  .copy-panel {
    right: 14px;
    bottom: 14px;
    width: calc(100vw - 28px);
  }
}
</style>
