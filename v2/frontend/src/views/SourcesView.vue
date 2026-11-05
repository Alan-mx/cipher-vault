<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { NAlert, NSpin } from "naive-ui";
import { knowledgeBaseApi } from "../api/knowledge-base";
import type {
  SourceContentChunk,
  SourceDetail,
  SourceLocator,
  SourceLocatorContent,
  SourceRevision,
  SourceSummary,
} from "../types/knowledge-base";
import { describeApiError } from "../utils/api-error";

const sources = ref<SourceSummary[]>([]);
const selected = ref<SourceDetail | null>(null);
const revisions = ref<SourceRevision[]>([]);
const revision = ref<number | null>(null);
const content = ref<SourceContentChunk | null>(null);
const locators = ref<SourceLocator[]>([]);
const locatorContent = ref<SourceLocatorContent | null>(null);
const title = ref("");
const kind = ref("web_page");
const url = ref("");
const text = ref("");
const error = ref<string | null>(null);
const loading = ref(false);

const selectedRevision = computed(
  () =>
    revisions.value.find((item) => item.revision === revision.value) ?? null,
);

const load = async (): Promise<void> => {
  try {
    sources.value = (await knowledgeBaseApi.sources({ pageSize: 100 })).items;
  } catch (caught) {
    error.value = describeApiError(caught);
  }
};

const open = async (source: SourceSummary): Promise<void> => {
  loading.value = true;
  error.value = null;
  try {
    selected.value = await knowledgeBaseApi.source(source.sourceId);
    revisions.value = (
      await knowledgeBaseApi.sourceRevisions(source.sourceId)
    ).items;
    await selectRevision(selected.value.currentRevision);
  } catch (caught) {
    error.value = describeApiError(caught);
  } finally {
    loading.value = false;
  }
};

const selectRevision = async (nextRevision: number): Promise<void> => {
  if (!selected.value) return;
  revision.value = nextRevision;
  locatorContent.value = null;
  content.value = await knowledgeBaseApi.sourceContent(
    selected.value.sourceId,
    nextRevision,
  );
  locators.value = (
    await knowledgeBaseApi.sourceLocators(selected.value.sourceId, nextRevision)
  ).items;
};

const openLocator = async (locator: SourceLocator): Promise<void> => {
  if (!selected.value || revision.value === null) return;
  try {
    locatorContent.value = await knowledgeBaseApi.sourceLocatorContent(
      selected.value.sourceId,
      revision.value,
      locator.locatorId,
    );
  } catch (caught) {
    error.value = describeApiError(caught);
  }
};

const createDraft = async (): Promise<void> => {
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
    error.value = describeApiError(caught);
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
    <NAlert v-if="error" type="error" :show-icon="false">{{ error }}</NAlert>
    <div class="layout">
      <aside aria-label="来源列表">
        <button
          v-for="item in sources"
          :key="item.sourceId"
          type="button"
          class="source-item"
          :class="{ active: selected?.sourceId === item.sourceId }"
          @click="open(item)"
        >
          <strong>{{ item.title }}</strong>
          <span>{{ item.kind }} · v{{ item.currentRevision }}</span>
        </button>
        <p v-if="sources.length === 0" class="empty">暂无来源。</p>
      </aside>
      <main>
        <section v-if="selected" class="detail">
          <header class="detail-header">
            <div>
              <h2>{{ selected.title }}</h2>
              <p class="muted">
                {{ selected.kind }} · 当前版本 {{ selected.currentRevision }}
              </p>
            </div>
            <label>
              <span>查看版本</span>
              <select
                aria-label="来源版本"
                :value="revision ?? undefined"
                @change="
                  selectRevision(
                    Number(($event.target as HTMLSelectElement).value),
                  )
                "
              >
                <option
                  v-for="item in revisions"
                  :key="item.sourceRevisionId"
                  :value="item.revision"
                >
                  v{{ item.revision }} ·
                  {{ new Date(item.createdAt).toLocaleString("zh-CN") }}
                </option>
              </select>
            </label>
          </header>

          <div v-if="loading" class="loading">
            <NSpin size="small" />
            正在读取来源
          </div>
          <div v-else class="source-browser">
            <nav aria-label="来源定位">
              <h3>章节与段落</h3>
              <button
                v-for="locator in locators"
                :key="locator.locatorId"
                type="button"
                :class="{
                  active:
                    locatorContent?.locator.locatorId === locator.locatorId,
                }"
                @click="openLocator(locator)"
              >
                <strong>{{ locator.label || locator.type }}</strong>
                <span>{{ locator.type }} · {{ locator.startOffset }}</span>
              </button>
              <p v-if="locators.length === 0" class="empty">
                当前版本没有章节或段落定位。
              </p>
            </nav>
            <section class="content" aria-live="polite">
              <h3>
                {{
                  locatorContent?.locator.label ||
                  selectedRevision?.title ||
                  "来源内容"
                }}
              </h3>
              <p v-if="locatorContent" class="muted">
                定位内容 · {{ locatorContent.offset }} -
                {{ locatorContent.nextOffset }} /
                {{ locatorContent.length }}
              </p>
              <p v-else-if="content" class="muted">
                全文片段 · {{ content.offset }} - {{ content.nextOffset }} /
                {{ content.totalLength }}
              </p>
              <pre>{{ locatorContent?.text ?? content?.text }}</pre>
            </section>
          </div>
        </section>

        <form class="create" @submit.prevent="createDraft">
          <h2>创建来源草稿</h2>
          <input v-model="title" placeholder="来源标题" required />
          <select v-model="kind" aria-label="来源类型">
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
  grid-template-columns: 280px minmax(0, 1fr);
  gap: 18px;
  margin-top: 16px;
}
aside {
  display: grid;
  align-content: start;
  gap: 6px;
}
.source-item {
  display: grid;
  gap: 4px;
  padding: 12px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  color: inherit;
  cursor: pointer;
  text-align: left;
}
.source-item.active {
  border-color: var(--cv-accent);
}
.source-item span,
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
.detail-header {
  display: flex;
  align-items: start;
  justify-content: space-between;
  gap: 16px;
}
.detail-header h2 {
  margin: 0;
  font-size: 18px;
}
.detail-header label {
  display: grid;
  gap: 5px;
}
.detail-header label span {
  color: var(--cv-muted);
  font-size: 11px;
}
.source-browser {
  display: grid;
  grid-template-columns: 220px minmax(0, 1fr);
  gap: 16px;
  margin-top: 18px;
}
.source-browser nav {
  display: grid;
  align-content: start;
  gap: 6px;
}
h3 {
  margin: 0 0 6px;
  font-size: 13px;
}
.source-browser nav button {
  display: grid;
  gap: 3px;
  padding: 9px;
  border: 1px solid var(--cv-border);
  border-radius: 6px;
  background: transparent;
  color: inherit;
  cursor: pointer;
  text-align: left;
}
.source-browser nav button.active {
  border-color: var(--cv-accent);
  color: var(--cv-accent);
}
.source-browser nav span {
  color: var(--cv-muted);
  font-size: 11px;
}
.content {
  min-width: 0;
}
.content pre {
  max-height: 520px;
  overflow: auto;
  padding: 12px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: color-mix(in srgb, var(--cv-bg) 65%, var(--cv-surface));
  line-height: 1.65;
  white-space: pre-wrap;
}
.create {
  display: grid;
  gap: 10px;
  margin-top: 16px;
}
.create h2 {
  margin: 0;
  font-size: 16px;
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
.create button {
  justify-self: start;
  background: var(--cv-accent);
  color: white;
  cursor: pointer;
}
.loading {
  display: flex;
  min-height: 160px;
  align-items: center;
  justify-content: center;
  gap: 8px;
  color: var(--cv-muted);
}
.empty {
  color: var(--cv-muted);
  font-size: 12px;
}
@media (max-width: 850px) {
  .page {
    padding: 18px;
  }
  .layout,
  .source-browser {
    grid-template-columns: 1fr;
  }
  .detail-header {
    flex-direction: column;
  }
}
</style>
