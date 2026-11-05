<script setup lang="ts">
import { ref } from "vue";
import { RouterLink } from "vue-router";
import { knowledgeBaseApi } from "../api/knowledge-base";

const centerId = ref("");
const graph = ref<Awaited<
  ReturnType<typeof knowledgeBaseApi.localGraph>
> | null>(null);
const error = ref<string | null>(null);

const load = async () => {
  error.value = null;
  try {
    graph.value = await knowledgeBaseApi.localGraph(centerId.value);
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "加载失败";
  }
};
</script>

<template>
  <section class="page">
    <header>
      <p class="eyebrow">Graph</p>
      <h1>知识网络</h1>
      <p class="muted">从一条知识开始查看已确认的局部关系。</p>
    </header>
    <form class="search" @submit.prevent="load">
      <input v-model="centerId" placeholder="Knowledge ID" required />
      <button type="submit">查看关系</button>
    </form>
    <p v-if="error" class="error">{{ error }}</p>
    <div v-if="graph" class="graph">
      <RouterLink
        v-for="node in graph.nodes"
        :key="node.knowledgeId"
        :to="`/knowledge/${node.knowledgeId}`"
        :class="{ center: node.isCenter }"
      >
        <strong>{{ node.title }}</strong>
        <span>{{ node.degree }} 条关系</span>
      </RouterLink>
    </div>
  </section>
</template>

<style scoped>
.page {
  padding: 28px;
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
.muted,
span {
  color: var(--cv-muted);
  font-size: 12px;
}
.search {
  display: flex;
  gap: 8px;
  margin: 20px 0;
}
input {
  flex: 1;
  min-height: 40px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-surface);
  color: var(--cv-text);
  padding: 0 12px;
}
button {
  padding: 0 14px;
  border: 0;
  border-radius: 7px;
  background: var(--cv-accent);
  color: white;
}
.graph {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 10px;
}
.graph a {
  display: grid;
  gap: 8px;
  padding: 14px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  color: inherit;
  text-decoration: none;
}
.graph a.center {
  border-color: var(--cv-accent);
}
.error {
  color: #b93f36;
}
@media (max-width: 700px) {
  .page {
    padding: 18px;
  }
}
</style>
