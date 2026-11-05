<script setup lang="ts">
import { onMounted, ref } from "vue";
import { useRoute } from "vue-router";
import { knowledgeBaseApi } from "../api/knowledge-base";
import type { KnowledgeDetail } from "../types/knowledge-base";

const route = useRoute();
const detail = ref<KnowledgeDetail | null>(null);
const body = ref("");
const error = ref<string | null>(null);

onMounted(async () => {
  try {
    const id = String(route.params.id);
    detail.value = await knowledgeBaseApi.knowledgeDetail(id);
    body.value = (
      await knowledgeBaseApi.knowledgeContent(id, detail.value.currentRevision)
    ).markdown;
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "加载失败";
  }
});
</script>

<template>
  <section class="page">
    <p v-if="error" class="error">{{ error }}</p>
    <template v-if="detail">
      <header>
        <p class="eyebrow">
          {{ detail.knowledgeTypeKey }} · v{{ detail.currentRevision }}
        </p>
        <h1>{{ detail.title }}</h1>
        <p class="summary">{{ detail.summary }}</p>
        <div class="tags">
          <span v-for="tag in detail.tags" :key="tag">{{ tag }}</span>
        </div>
      </header>
      <article class="content">{{ body }}</article>
      <section v-if="detail.sourceReferences.length" class="references">
        <h2>来源依据</h2>
        <article
          v-for="sourceRef in detail.sourceReferences"
          :key="sourceRef.sourceReferenceId"
        >
          <strong
            >{{ sourceRef.sourceId.slice(0, 8) }} · v{{
              sourceRef.sourceRevision
            }}</strong
          >
          <p>{{ sourceRef.excerpt }}</p>
        </article>
      </section>
    </template>
  </section>
</template>

<style scoped>
.page {
  max-width: 900px;
  margin: 0 auto;
  padding: 28px;
}
.eyebrow {
  margin: 0 0 8px;
  color: var(--cv-accent);
  font-size: 11px;
  text-transform: uppercase;
}
h1 {
  margin: 0;
  font-size: 30px;
}
.summary {
  color: var(--cv-muted);
}
.tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
.tags span {
  padding: 3px 8px;
  border: 1px solid var(--cv-border);
  border-radius: 6px;
  font-size: 12px;
}
.content {
  margin-top: 24px;
  padding: 22px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  white-space: pre-wrap;
  line-height: 1.7;
}
.references {
  margin-top: 24px;
}
.references article {
  padding: 12px 0;
  border-top: 1px solid var(--cv-border);
}
.references p {
  color: var(--cv-muted);
}
.error {
  color: #b93f36;
}
</style>
