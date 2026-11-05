<script setup lang="ts">
import { nextTick, ref, watch } from "vue";
import { NButton, NIcon, NModal, NSpin } from "naive-ui";
import { Search, X } from "@lucide/vue";
import { knowledgeBaseApi } from "../../api/knowledge-base";
import type { KnowledgeSummary } from "../../types/knowledge-base";

const props = defineProps<{
  show: boolean;
}>();

const emit = defineEmits<{
  "update:show": [value: boolean];
  open: [knowledgeId: string];
}>();

const query = ref("");
const mode = ref<"keyword" | "fulltext">("keyword");
const hits = ref<KnowledgeSummary[]>([]);
const loading = ref(false);
const error = ref<string | null>(null);

const submit = async (): Promise<void> => {
  if (!query.value.trim()) {
    hits.value = [];
    return;
  }

  loading.value = true;
  error.value = null;
  try {
    const page = await knowledgeBaseApi.searchKnowledge({
      query: query.value.trim(),
      mode: mode.value,
      pageSize: 30,
    });
    hits.value = page.items;
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "搜索失败";
  } finally {
    loading.value = false;
  }
};

watch(
  () => props.show,
  async (show) => {
    if (show) {
      await nextTick();
      document
        .querySelector<HTMLInputElement>(".knowledge-search input")
        ?.focus();
    }
  },
);
</script>

<template>
  <NModal :show="show" @update:show="emit('update:show', $event)">
    <section
      class="knowledge-search"
      role="dialog"
      aria-modal="true"
      aria-label="搜索知识"
    >
      <header>
        <input
          v-model="query"
          placeholder="搜索知识标题、标签或正文"
          aria-label="知识搜索词"
          @keyup.enter="submit"
        />
        <select v-model="mode" aria-label="检索模式">
          <option value="keyword">关键词</option>
          <option value="fulltext">全文</option>
        </select>
        <NButton
          type="primary"
          :loading="loading"
          :disabled="!query.trim()"
          @click="submit"
        >
          <template #icon>
            <NIcon :component="Search" aria-hidden="true" />
          </template>
          搜索
        </NButton>
        <NButton
          quaternary
          circle
          aria-label="关闭搜索"
          @click="emit('update:show', false)"
        >
          <template #icon>
            <NIcon :component="X" aria-hidden="true" />
          </template>
        </NButton>
      </header>

      <p v-if="error" class="status error">{{ error }}</p>
      <div v-else-if="loading" class="status">
        <NSpin size="small" />
        正在搜索
      </div>
      <div v-else-if="hits.length" class="results">
        <button
          v-for="hit in hits"
          :key="hit.knowledgeId"
          type="button"
          @click="emit('open', hit.knowledgeId)"
        >
          <strong>{{ hit.title }}</strong>
          <span>{{ hit.summary || "暂无摘要" }}</span>
          <small>{{ hit.knowledgeTypeKey }} · v{{ hit.revision }}</small>
        </button>
      </div>
      <p v-else class="status">
        {{ query.trim() ? "没有匹配的知识。" : "输入检索词后开始搜索。" }}
      </p>
    </section>
  </NModal>
</template>

<style scoped>
.knowledge-search {
  display: grid;
  width: min(94vw, 760px);
  max-height: min(82vh, 720px);
  grid-template-rows: auto 1fr;
  gap: 14px;
  padding: 18px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}

header {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 120px auto auto;
  gap: 8px;
}

input,
select {
  min-width: 0;
  min-height: 38px;
  padding: 0 11px;
  border: 1px solid var(--cv-border);
  border-radius: 6px;
  background: var(--cv-surface);
  color: var(--cv-text);
}

input:focus,
select:focus {
  border-color: var(--cv-accent);
  outline: 2px solid color-mix(in srgb, var(--cv-accent) 20%, transparent);
}

.results {
  display: grid;
  gap: 2px;
  overflow: auto;
}

.results button {
  display: grid;
  gap: 4px;
  padding: 11px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: inherit;
  text-align: left;
}

.results button:hover,
.results button:focus-visible {
  background: color-mix(in srgb, var(--cv-accent) 8%, transparent);
}

.results span,
.results small,
.status {
  color: var(--cv-muted);
}

.results span {
  overflow: hidden;
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.status {
  display: flex;
  min-height: 120px;
  align-items: center;
  justify-content: center;
  gap: 8px;
}

.error {
  color: #b93f36;
}

@media (max-width: 640px) {
  header {
    grid-template-columns: 1fr 1fr;
  }

  header input {
    grid-column: 1 / -1;
  }
}
</style>
