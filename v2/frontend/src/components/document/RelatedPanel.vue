<script setup lang="ts">
import { useRouter } from "vue-router";
import {
  NAlert,
  NButton,
  NEmpty,
  NIcon,
  NSpin,
  NTag,
  NTooltip,
} from "naive-ui";
import { RefreshCw } from "@lucide/vue";
import { useDocumentStore } from "../../stores/document";
import ConfirmedRelationsPanel from "./ConfirmedRelationsPanel.vue";

const documents = useDocumentStore();
const router = useRouter();

const openDocument = async (documentId: string): Promise<void> => {
  await router.push({ name: "document", params: { id: documentId } });
};

const refresh = (): Promise<void> => documents.loadRelated(true);

const formatScore = (score: number): string =>
  Number.isInteger(score)
    ? score.toFixed(0)
    : score.toFixed(2).replace(/0+$/, "").replace(/\.$/, "");
</script>

<template>
  <section class="related-panel" aria-label="相关文档">
    <div class="related-toolbar">
      <span class="related-count">{{ documents.relatedItems.length }} 条</span>
      <NTooltip trigger="hover">
        <template #trigger>
          <NButton
            quaternary
            circle
            size="tiny"
            aria-label="刷新相关推荐"
            :loading="documents.relatedStatus === 'loading'"
            @click="refresh"
          >
            <template #icon>
              <NIcon :component="RefreshCw" aria-hidden="true" />
            </template>
          </NButton>
        </template>
        刷新相关推荐
      </NTooltip>
    </div>

    <div v-if="documents.relatedStatus === 'loading'" class="related-loading">
      <NSpin size="small" />
      <span>正在查找相关文档…</span>
    </div>
    <NAlert
      v-else-if="documents.relatedStatus === 'error'"
      type="error"
      :show-icon="false"
    >
      <p class="related-error">
        {{ documents.relatedError?.message ?? "无法读取相关文档。" }}
      </p>
      <NButton size="tiny" secondary @click="refresh">重试</NButton>
    </NAlert>
    <div v-else-if="documents.relatedItems.length" class="related-list">
      <button
        v-for="item in documents.relatedItems"
        :key="item.documentId"
        type="button"
        @click="openDocument(item.documentId)"
      >
        <div class="related-heading">
          <strong>{{ item.title }}</strong>
          <span class="related-score"> {{ formatScore(item.score) }} 分 </span>
        </div>
        <p class="related-path">{{ item.virtualPath }}</p>
        <div v-if="item.tags.length" class="related-tags">
          <NTag
            v-for="tag in item.tags.slice(0, 3)"
            :key="tag"
            size="tiny"
            :bordered="false"
          >
            {{ tag }}
          </NTag>
          <span v-if="item.tags.length > 3" class="related-more">
            +{{ item.tags.length - 3 }}
          </span>
        </div>
        <ul class="related-reasons">
          <li v-for="reason in item.reasons" :key="reason.code">
            {{ reason.label }}
          </li>
        </ul>
      </button>
    </div>
    <NEmpty v-else description="暂无相关文档" size="small" />
    <ConfirmedRelationsPanel
      v-if="documents.documentId"
      :document-id="documents.documentId"
    />
  </section>
</template>

<style scoped>
.related-panel {
  display: grid;
  gap: 8px;
}

.related-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  min-height: 24px;
}

.related-count {
  color: var(--cv-muted);
  font-size: 11px;
}

.related-loading {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 16px 4px;
  color: var(--cv-muted);
  font-size: 12px;
}

.related-error {
  margin: 0 0 8px;
  font-size: 12px;
}

.related-list {
  display: grid;
  gap: 4px;
}

.related-list button {
  display: grid;
  gap: 5px;
  width: 100%;
  min-width: 0;
  padding: 9px 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: inherit;
  text-align: left;
}

.related-list button:hover {
  background: color-mix(in srgb, var(--cv-accent) 7%, transparent);
}

.related-heading {
  display: flex;
  min-width: 0;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
}

.related-heading strong {
  overflow: hidden;
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.related-score {
  flex: none;
  color: var(--cv-muted);
  font-size: 10px;
}

.related-path {
  overflow: hidden;
  margin: 0;
  color: var(--cv-muted);
  font-size: 10px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.related-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.related-more {
  align-self: center;
  color: var(--cv-muted);
  font-size: 10px;
}

.related-reasons {
  display: grid;
  gap: 2px;
  margin: 0;
  padding: 0;
  color: var(--cv-muted);
  font-size: 10px;
  list-style: none;
}
</style>
