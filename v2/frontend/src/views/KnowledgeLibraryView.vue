<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { RouterLink } from "vue-router";
import { knowledgeBaseApi } from "../api/knowledge-base";
import type {
  DraftMutation,
  KnowledgeSummary,
  PagedResult,
} from "../types/knowledge-base";

const maxSelection = 20;
const maxTopicLength = 193;

const query = ref("");
const mode = ref<"keyword" | "fulltext">("keyword");
const page = ref<PagedResult<KnowledgeSummary> | null>(null);
const loading = ref(false);
const error = ref<string | null>(null);
const selectedIds = ref<string[]>([]);
const retrospectiveOpen = ref(false);
const topic = ref("");
const creating = ref(false);
const createError = ref<string | null>(null);
const createdDraft = ref<DraftMutation | null>(null);
const submissionKey = ref<string | null>(null);

const activeItems = computed(
  () => page.value?.items.filter((item) => item.status === "active") ?? [],
);
const selectedSet = computed(() => new Set(selectedIds.value));
const topicLength = computed(() => Array.from(topic.value.trim()).length);
const canCreate = computed(
  () =>
    selectedIds.value.length >= 1 &&
    selectedIds.value.length <= maxSelection &&
    topicLength.value >= 1 &&
    topicLength.value <= maxTopicLength &&
    !creating.value,
);

const load = async () => {
  loading.value = true;
  error.value = null;
  try {
    page.value = query.value.trim()
      ? ((await knowledgeBaseApi.searchKnowledge({
          query: query.value,
          mode: mode.value,
          pageSize: 50,
        })) as PagedResult<KnowledgeSummary>)
      : await knowledgeBaseApi.knowledge({ status: "active", pageSize: 50 });
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "加载失败";
  } finally {
    loading.value = false;
  }
};

const toggleSelection = (knowledgeId: string) => {
  if (selectedIds.value.includes(knowledgeId)) {
    selectedIds.value = selectedIds.value.filter(
      (item) => item !== knowledgeId,
    );
    return;
  }
  if (selectedIds.value.length < maxSelection) {
    selectedIds.value = [...selectedIds.value, knowledgeId];
  }
};

const clearSelection = () => {
  selectedIds.value = [];
};

const openRetrospective = () => {
  if (selectedIds.value.length === 0) {
    return;
  }
  retrospectiveOpen.value = true;
  createError.value = null;
  createdDraft.value = null;
  submissionKey.value = null;
};

const closeRetrospective = () => {
  if (!creating.value) {
    retrospectiveOpen.value = false;
  }
};

const createRetrospective = async () => {
  if (!canCreate.value) {
    return;
  }
  creating.value = true;
  createError.value = null;
  submissionKey.value ??= crypto.randomUUID();
  try {
    createdDraft.value =
      await knowledgeBaseApi.createKnowledgeRetrospectiveDraft(
        {
          topic: topic.value.trim(),
          knowledgeIds: [...selectedIds.value],
        },
        submissionKey.value,
      );
  } catch (caught) {
    createError.value =
      caught instanceof Error ? caught.message : "创建复盘草稿失败";
  } finally {
    creating.value = false;
  }
};

watch(topic, () => {
  submissionKey.value = null;
  createdDraft.value = null;
});
watch(
  selectedIds,
  () => {
    submissionKey.value = null;
    createdDraft.value = null;
  },
  { deep: true },
);

onMounted(load);

defineExpose({ selectedIds });
</script>

<template>
  <section class="page">
    <header class="page-header">
      <div>
        <p class="eyebrow">Knowledge</p>
        <h1>知识库</h1>
      </div>
      <RouterLink class="button" to="/inbox">新建知识草稿</RouterLink>
    </header>
    <form class="search" @submit.prevent="load">
      <input v-model="query" placeholder="搜索标题、标签或正文" />
      <select v-model="mode" aria-label="检索模式">
        <option value="keyword">关键词</option>
        <option value="fulltext">全文</option>
      </select>
      <button type="submit" :disabled="loading">
        {{ loading ? "检索中" : "搜索" }}
      </button>
    </form>
    <p v-if="error" class="error">{{ error }}</p>
    <div v-if="selectedIds.length" class="selection-bar">
      <span>已选择 {{ selectedIds.length }} / {{ maxSelection }} 条</span>
      <div class="selection-actions">
        <button type="button" @click="openRetrospective">生成复盘草稿</button>
        <button type="button" class="secondary" @click="clearSelection">
          清空
        </button>
      </div>
    </div>
    <div class="list">
      <div
        v-for="item in activeItems"
        :key="item.knowledgeId"
        class="knowledge-row"
        :class="{ selected: selectedSet.has(item.knowledgeId) }"
      >
        <label class="selection">
          <input
            type="checkbox"
            :checked="selectedSet.has(item.knowledgeId)"
            :disabled="
              !selectedSet.has(item.knowledgeId) &&
              selectedIds.length >= maxSelection
            "
            :aria-label="`选择 ${item.title}`"
            @change="toggleSelection(item.knowledgeId)"
          />
        </label>
        <RouterLink
          class="knowledge-link"
          :to="`/knowledge/${item.knowledgeId}`"
        >
          <div>
            <h2>{{ item.title }}</h2>
            <p>{{ item.summary || "暂无摘要" }}</p>
          </div>
          <div class="meta">
            <span>{{ item.knowledgeTypeKey }}</span>
            <span>v{{ item.revision }}</span>
          </div>
        </RouterLink>
      </div>
      <p v-if="!loading && activeItems.length === 0" class="muted">
        没有匹配的知识。
      </p>
    </div>

    <div
      v-if="retrospectiveOpen"
      class="modal-backdrop"
      @click.self="closeRetrospective"
    >
      <section
        class="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="retrospective-title"
      >
        <header class="modal-header">
          <div>
            <p class="eyebrow">Reflection draft</p>
            <h2 id="retrospective-title">生成复盘草稿</h2>
          </div>
          <button
            type="button"
            class="icon-button"
            aria-label="关闭"
            :disabled="creating"
            @click="closeRetrospective"
          >
            ×
          </button>
        </header>
        <p class="muted">
          将按选择顺序引用 {{ selectedIds.length }} 条知识，并保留来源依据。
        </p>
        <form @submit.prevent="createRetrospective">
          <label class="field">
            <span>复盘主题</span>
            <input
              v-model="topic"
              autofocus
              placeholder="例如：v1.3 P0 交付复盘"
            />
          </label>
          <div class="field-meta">
            <span>1～{{ maxTopicLength }} 个字符</span>
            <span>{{ topicLength }} / {{ maxTopicLength }}</span>
          </div>
          <p v-if="createError" class="error" role="alert">
            {{ createError }}
          </p>
          <div v-if="createdDraft" class="success" role="status">
            <p>复盘草稿已创建。</p>
            <RouterLink
              class="button"
              :to="{
                path: '/review',
                query: { status: 'draft', draftId: createdDraft.draftId },
              }"
            >
              打开复盘草稿
            </RouterLink>
          </div>
          <div class="modal-actions">
            <button type="submit" :disabled="!canCreate">
              {{ creating ? "创建中" : "创建草稿" }}
            </button>
            <button
              type="button"
              class="secondary"
              :disabled="creating"
              @click="closeRetrospective"
            >
              取消
            </button>
          </div>
        </form>
      </section>
    </div>
  </section>
</template>

<style scoped>
.page {
  padding: 28px;
}
.page-header {
  display: flex;
  align-items: end;
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
  margin: 0 0 18px;
  font-size: 28px;
}
.button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: 9px 14px;
  border-radius: 7px;
  background: var(--cv-accent);
  color: white;
  text-decoration: none;
}
.search {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 130px auto;
  gap: 8px;
  margin-bottom: 16px;
}
input,
select,
button {
  min-height: 40px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-surface);
  color: var(--cv-text);
  padding: 0 12px;
}
button {
  cursor: pointer;
}
button:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}
.selection-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 12px;
  padding: 10px 12px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  color: var(--cv-muted);
  font-size: 13px;
}
.selection-actions {
  display: flex;
  gap: 8px;
}
.list {
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  overflow: hidden;
}
.knowledge-row {
  display: flex;
  align-items: stretch;
  border-bottom: 1px solid var(--cv-border);
}
.knowledge-row:last-child {
  border-bottom: 0;
}
.knowledge-row.selected {
  background: color-mix(in srgb, var(--cv-accent) 7%, var(--cv-surface));
}
.selection {
  display: flex;
  align-items: center;
  padding-left: 14px;
}
.selection input {
  width: 18px;
  min-height: 18px;
  padding: 0;
}
.knowledge-link {
  display: flex;
  flex: 1;
  justify-content: space-between;
  gap: 20px;
  min-width: 0;
  padding: 16px;
  color: inherit;
  text-decoration: none;
}
h2 {
  margin: 0;
  font-size: 15px;
}
.knowledge-link p {
  margin: 5px 0 0;
  color: var(--cv-muted);
  font-size: 13px;
}
.meta {
  display: flex;
  gap: 8px;
  color: var(--cv-muted);
  font-size: 12px;
  white-space: nowrap;
}
.muted {
  color: var(--cv-muted);
}
.error {
  color: #b93f36;
}
.modal-backdrop {
  position: fixed;
  inset: 0;
  z-index: 40;
  display: grid;
  place-items: center;
  padding: 20px;
  background: rgb(0 0 0 / 45%);
}
.modal {
  width: min(560px, 100%);
  padding: 20px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  color: var(--cv-text);
  box-shadow: 0 18px 60px rgb(0 0 0 / 30%);
}
.modal-header {
  display: flex;
  align-items: start;
  justify-content: space-between;
  gap: 16px;
}
.modal-header h2 {
  font-size: 20px;
}
.icon-button {
  width: 36px;
  min-height: 36px;
  padding: 0;
  font-size: 22px;
  line-height: 1;
}
.field {
  display: grid;
  gap: 6px;
  margin-top: 16px;
  font-size: 13px;
}
.field input {
  width: 100%;
}
.field-meta {
  display: flex;
  justify-content: space-between;
  margin-top: 5px;
  color: var(--cv-muted);
  font-size: 12px;
}
.modal-actions {
  display: flex;
  gap: 8px;
  margin-top: 18px;
}
.secondary {
  border: 1px solid var(--cv-border);
  background: transparent;
  color: var(--cv-text);
}
.success {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-top: 14px;
  padding: 12px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
}
.success p {
  margin: 0;
}
@media (max-width: 700px) {
  .page {
    padding: 18px;
  }
  .search {
    grid-template-columns: 1fr;
  }
  .page-header {
    align-items: start;
    flex-direction: column;
  }
  .selection-bar,
  .success {
    align-items: stretch;
    flex-direction: column;
  }
  .knowledge-link {
    gap: 8px;
    padding: 14px;
  }
  .meta {
    flex-direction: column;
  }
}
</style>
