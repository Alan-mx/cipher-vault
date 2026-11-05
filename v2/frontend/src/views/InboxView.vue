<script setup lang="ts">
import { onMounted, ref } from "vue";
import { knowledgeBaseApi } from "../api/knowledge-base";
import type { DraftListItem, KnowledgeModel } from "../types/knowledge-base";

const model = ref<KnowledgeModel | null>(null);
const drafts = ref<DraftListItem[]>([]);
const title = ref("");
const content = ref("");
const typeId = ref("");
const error = ref<string | null>(null);

const load = async () => {
  model.value = await knowledgeBaseApi.knowledgeModel();
  typeId.value ||=
    model.value.knowledgeTypes.find((item) => item.isEnabled)?.typeId ?? "";
  drafts.value = (
    await knowledgeBaseApi.drafts({
      status: "draft",
      targetObjectType: "knowledge",
      pageSize: 100,
    })
  ).items;
};

const capture = async () => {
  error.value = null;
  try {
    await knowledgeBaseApi.createDraft({
      targetObjectType: "knowledge",
      operationType: "create",
      payload: {
        title: title.value,
        summary: "",
        knowledgeTypeId: typeId.value,
        templateId: null,
        contentMarkdown: content.value,
        properties: {},
        tags: [],
        sourceReferences: [],
      },
    });
    title.value = "";
    content.value = "";
    await load();
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "保存失败";
  }
};

onMounted(load);
</script>

<template>
  <section class="page">
    <header>
      <p class="eyebrow">Inbox</p>
      <h1>收集箱</h1>
      <p class="muted">先记录为草稿，正式内容由你审核后写入。</p>
    </header>
    <form class="capture" @submit.prevent="capture">
      <input v-model="title" placeholder="标题" required />
      <select v-model="typeId">
        <option
          v-for="type in model?.knowledgeTypes.filter((item) => item.isEnabled)"
          :key="type.typeId"
          :value="type.typeId"
        >
          {{ type.name }}
        </option>
      </select>
      <textarea
        v-model="content"
        rows="8"
        placeholder="记录想法、摘录或待整理内容…"
      />
      <button type="submit">保存到草稿</button>
    </form>
    <p v-if="error" class="error">{{ error }}</p>
    <h2>未整理草稿</h2>
    <div class="drafts">
      <article v-for="draft in drafts" :key="draft.draftId">
        <strong>{{ draft.title || "未命名" }}</strong>
        <span>{{ draft.updatedAt }}</span>
      </article>
      <p v-if="drafts.length === 0" class="muted">暂无未整理内容。</p>
    </div>
  </section>
</template>

<style scoped>
.page {
  max-width: 900px;
  margin: 0 auto;
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
.muted {
  color: var(--cv-muted);
}
.capture {
  display: grid;
  gap: 10px;
  margin: 22px 0 28px;
  padding: 16px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}
input,
select,
textarea,
button {
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-bg);
  color: var(--cv-text);
  padding: 10px 12px;
}
button {
  justify-self: start;
  background: var(--cv-accent);
  color: white;
  cursor: pointer;
}
.drafts {
  display: grid;
  gap: 8px;
}
.drafts article {
  display: flex;
  justify-content: space-between;
  padding: 12px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}
.drafts span {
  color: var(--cv-muted);
  font-size: 12px;
}
.error {
  color: #b93f36;
}
</style>
