<script setup lang="ts">
import { onMounted, ref } from "vue";
import { knowledgeBaseApi } from "../api/knowledge-base";
import type {
  SourceContentChunk,
  SourceDetail,
  SourceSummary,
} from "../types/knowledge-base";

const sources = ref<SourceSummary[]>([]);
const selected = ref<SourceDetail | null>(null);
const content = ref<SourceContentChunk | null>(null);
const title = ref("");
const kind = ref("web_page");
const url = ref("");
const text = ref("");
const error = ref<string | null>(null);

const load = async () => {
  sources.value = (await knowledgeBaseApi.sources({ pageSize: 100 })).items;
};

const open = async (source: SourceSummary) => {
  selected.value = await knowledgeBaseApi.source(source.sourceId);
  content.value = await knowledgeBaseApi.sourceContent(
    source.sourceId,
    selected.value.currentRevision,
  );
};

const createDraft = async () => {
  error.value = null;
  try {
    const metadata = kind.value === "web_page" ? { url: url.value } : {};
    await knowledgeBaseApi.createDraft({
      targetObjectType: "source",
      operationType: "create",
      payload: {
        sourceKind: kind.value,
        title: title.value,
        metadata,
        contentFormat: "markdown",
        contentText: text.value,
      },
    });
    title.value = "";
    url.value = "";
    text.value = "";
    await load();
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "创建失败";
  }
};

onMounted(load);
</script>

<template>
  <section class="page">
    <header>
      <p class="eyebrow">Sources</p>
      <h1>来源</h1>
    </header>
    <div class="layout">
      <aside>
        <article
          v-for="item in sources"
          :key="item.sourceId"
          @click="open(item)"
        >
          <strong>{{ item.title }}</strong>
          <span>{{ item.kind }} · v{{ item.currentRevision }}</span>
        </article>
      </aside>
      <main>
        <section v-if="selected" class="detail">
          <h2>{{ selected.title }}</h2>
          <p class="muted">
            {{ selected.kind }} · 当前版本 {{ selected.currentRevision }}
          </p>
          <pre>{{ content?.text }}</pre>
        </section>
        <form class="create" @submit.prevent="createDraft">
          <h2>创建来源草稿</h2>
          <input v-model="title" placeholder="来源标题" required />
          <select v-model="kind">
            <option value="web_page">网页</option>
            <option value="local_file">本地文件</option>
            <option value="book">书籍</option>
            <option value="podcast">播客</option>
            <option value="other">其他</option>
          </select>
          <input
            v-if="kind === 'web_page'"
            v-model="url"
            placeholder="https://…"
            required
          />
          <textarea
            v-model="text"
            rows="8"
            placeholder="来源内容快照"
            required
          />
          <button type="submit">保存为草稿</button>
        </form>
        <p v-if="error" class="error">{{ error }}</p>
      </main>
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
  margin: 0 0 18px;
  font-size: 28px;
}
.layout {
  display: grid;
  grid-template-columns: 300px minmax(0, 1fr);
  gap: 18px;
}
aside {
  display: grid;
  align-content: start;
  gap: 6px;
}
aside article {
  display: grid;
  gap: 4px;
  padding: 12px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  cursor: pointer;
}
aside span,
.muted {
  color: var(--cv-muted);
  font-size: 12px;
}
.detail,
.create {
  padding: 16px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}
.detail pre {
  max-height: 420px;
  overflow: auto;
  white-space: pre-wrap;
}
.create {
  display: grid;
  gap: 10px;
  margin-top: 16px;
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
.error {
  color: #b93f36;
}
@media (max-width: 800px) {
  .page {
    padding: 18px;
  }
  .layout {
    grid-template-columns: 1fr;
  }
}
</style>
