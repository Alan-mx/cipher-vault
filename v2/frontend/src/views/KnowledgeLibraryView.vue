<script setup lang="ts">
import { onMounted, ref } from "vue";
import { RouterLink } from "vue-router";
import { knowledgeBaseApi } from "../api/knowledge-base";
import type { KnowledgeSummary, PagedResult } from "../types/knowledge-base";

const query = ref("");
const mode = ref<"keyword" | "fulltext">("keyword");
const page = ref<PagedResult<KnowledgeSummary> | null>(null);
const loading = ref(false);
const error = ref<string | null>(null);

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
      : await knowledgeBaseApi.knowledge({ pageSize: 50 });
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "加载失败";
  } finally {
    loading.value = false;
  }
};

onMounted(load);
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
    <div class="list">
      <RouterLink
        v-for="item in page?.items ?? []"
        :key="item.knowledgeId"
        class="knowledge-row"
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
      <p v-if="!loading && page?.items.length === 0" class="muted">
        没有匹配的知识。
      </p>
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
.list {
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  overflow: hidden;
}
.knowledge-row {
  display: flex;
  justify-content: space-between;
  gap: 20px;
  padding: 16px;
  border-bottom: 1px solid var(--cv-border);
  color: inherit;
  text-decoration: none;
}
.knowledge-row:last-child {
  border-bottom: 0;
}
h2 {
  margin: 0;
  font-size: 15px;
}
.knowledge-row p {
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
}
</style>
