<script setup lang="ts">
import { onMounted, ref } from "vue";
import { knowledgeBaseApi } from "../api/knowledge-base";
import type { KnowledgeModel } from "../types/knowledge-base";

const model = ref<KnowledgeModel | null>(null);
const json = ref("");
const message = ref("");

const load = async () => {
  model.value = await knowledgeBaseApi.knowledgeModel();
  json.value = JSON.stringify(model.value, null, 2);
};

const save = async () => {
  if (!model.value) return;
  try {
    const parsed = JSON.parse(json.value) as KnowledgeModel;
    parsed.version = model.value.version;
    model.value = await knowledgeBaseApi.replaceKnowledgeModel(parsed);
    json.value = JSON.stringify(model.value, null, 2);
    message.value = "模型已保存";
  } catch (caught) {
    message.value = caught instanceof Error ? caught.message : "保存失败";
  }
};

onMounted(load);
</script>

<template>
  <section class="page">
    <header>
      <p class="eyebrow">Knowledge Model</p>
      <h1>知识模型</h1>
      <p class="muted">维护类型、属性、模板、关系、来源规则和工作流约束。</p>
    </header>
    <div v-if="model" class="summary">
      <span>版本 {{ model.version }}</span>
      <span>{{ model.knowledgeTypes.length }} 个类型</span>
      <span>{{ model.relationTypes.length }} 个关系</span>
    </div>
    <textarea v-model="json" spellcheck="false" />
    <div class="actions">
      <button type="button" @click="save">保存模型</button
      ><span>{{ message }}</span>
    </div>
  </section>
</template>

<style scoped>
.page {
  max-width: 1000px;
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
.summary {
  display: flex;
  gap: 10px;
  margin: 18px 0;
}
.summary span {
  padding: 6px 10px;
  border: 1px solid var(--cv-border);
  border-radius: 6px;
  background: var(--cv-surface);
}
textarea {
  width: 100%;
  min-height: 520px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  color: var(--cv-text);
  padding: 14px;
  font-family: ui-monospace, monospace;
}
.actions {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-top: 12px;
}
button {
  padding: 9px 14px;
  border: 0;
  border-radius: 7px;
  background: var(--cv-accent);
  color: white;
  cursor: pointer;
}
@media (max-width: 700px) {
  .page {
    padding: 18px;
  }
}
</style>
