<script setup lang="ts">
import { onMounted, ref } from "vue";
import { RouterLink } from "vue-router";
import { knowledgeBaseApi } from "../api/knowledge-base";
import type { DashboardView } from "../types/knowledge-base";

const dashboard = ref<DashboardView | null>(null);
const error = ref<string | null>(null);

onMounted(async () => {
  try {
    dashboard.value = await knowledgeBaseApi.dashboard();
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "加载失败";
  }
});
</script>

<template>
  <section class="page">
    <header class="page-header">
      <div>
        <p class="eyebrow">Overview</p>
        <h1>首页</h1>
        <p class="muted">最近知识、来源、待审核草稿和未整理内容。</p>
      </div>
      <RouterLink class="primary-link" to="/inbox">快速记录</RouterLink>
    </header>

    <p v-if="error" class="error">{{ error }}</p>
    <div v-if="dashboard" class="stat-grid">
      <article v-for="(value, name) in dashboard.counts" :key="name">
        <span>{{ name }}</span>
        <strong>{{ value }}</strong>
      </article>
    </div>

    <div v-if="dashboard" class="dashboard-grid">
      <section class="panel">
        <header>
          <h2>最近知识</h2>
          <RouterLink to="/knowledge">全部</RouterLink>
        </header>
        <RouterLink
          v-for="item in dashboard.recentKnowledge"
          :key="item.knowledgeId"
          class="row-link"
          :to="`/knowledge/${item.knowledgeId}`"
        >
          <strong>{{ item.title }}</strong>
          <span>{{ item.summary || "暂无摘要" }}</span>
        </RouterLink>
        <p v-if="dashboard.recentKnowledge.length === 0" class="muted">
          暂无知识。
        </p>
      </section>

      <section class="panel">
        <header>
          <h2>最近来源</h2>
          <RouterLink to="/sources">全部</RouterLink>
        </header>
        <RouterLink
          v-for="item in dashboard.recentSources"
          :key="item.sourceId"
          class="row-link"
          to="/sources"
        >
          <strong>{{ item.title }}</strong>
          <span>{{ item.kind }} · v{{ item.currentRevision }}</span>
        </RouterLink>
        <p v-if="dashboard.recentSources.length === 0" class="muted">
          暂无来源。
        </p>
      </section>

      <section class="panel">
        <header>
          <h2>待审核</h2>
          <RouterLink to="/review">处理</RouterLink>
        </header>
        <RouterLink
          v-for="item in dashboard.pendingDrafts"
          :key="item.draftId"
          class="row-link"
          to="/review"
        >
          <strong>{{ item.title || item.targetObjectType }}</strong>
          <span>{{ item.operationType }} · {{ item.sourceActor }}</span>
        </RouterLink>
        <p v-if="dashboard.pendingDrafts.length === 0" class="muted">
          没有待审核内容。
        </p>
      </section>

      <section class="panel">
        <header>
          <h2>收集箱</h2>
          <RouterLink to="/inbox">打开</RouterLink>
        </header>
        <RouterLink
          v-for="item in dashboard.inbox"
          :key="item.draftId"
          class="row-link"
          to="/inbox"
        >
          <strong>{{ item.title || "未命名记录" }}</strong>
          <span>{{ item.updatedAt }}</span>
        </RouterLink>
        <p v-if="dashboard.inbox.length === 0" class="muted">收集箱为空。</p>
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
  gap: 20px;
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
  font-size: 14px;
}
.muted {
  color: var(--cv-muted);
}
.primary-link {
  padding: 9px 14px;
  border-radius: 7px;
  background: var(--cv-accent);
  color: white;
  text-decoration: none;
}
.stat-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 10px;
  margin: 24px 0;
}
.stat-grid article {
  padding: 14px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}
.stat-grid span {
  display: block;
  color: var(--cv-muted);
  font-size: 12px;
}
.stat-grid strong {
  display: block;
  margin-top: 5px;
  font-size: 24px;
}
.dashboard-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 14px;
}
.panel {
  padding: 16px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}
.panel header {
  display: flex;
  justify-content: space-between;
  margin-bottom: 10px;
}
.panel header a,
.row-link {
  color: inherit;
  text-decoration: none;
}
.row-link {
  display: grid;
  gap: 3px;
  padding: 10px 0;
  border-top: 1px solid var(--cv-border);
}
.row-link span {
  overflow: hidden;
  color: var(--cv-muted);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.error {
  color: #b93f36;
}
@media (max-width: 800px) {
  .page {
    padding: 18px;
  }
  .stat-grid,
  .dashboard-grid {
    grid-template-columns: 1fr;
  }
}
</style>
