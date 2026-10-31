<script setup lang="ts">
import { formatDistanceToNow } from "date-fns";
import { zhCN } from "date-fns/locale";
import { NEmpty, NTag } from "naive-ui";
import type { CatalogItem } from "../../types/api";

defineProps<{
  items: CatalogItem[];
}>();

const updatedLabel = (value: string): string =>
  formatDistanceToNow(new Date(value), {
    addSuffix: true,
    locale: zhCN,
  });
</script>

<template>
  <div v-if="items.length" class="document-list">
    <RouterLink
      v-for="item in items"
      :key="item.documentId"
      class="document-row"
      :to="{ name: 'document', params: { id: item.documentId } }"
    >
      <div class="document-main">
        <div class="document-title-line">
          <strong>{{ item.title }}</strong>
          <NTag size="tiny" :bordered="false">
            {{ item.format.toUpperCase() }}
          </NTag>
        </div>
        <p>{{ item.virtualPath }}</p>
        <div v-if="item.tags.length" class="document-tags">
          <span v-for="tag in item.tags" :key="tag">{{ tag }}</span>
        </div>
      </div>
      <div class="document-meta">
        <span>R{{ item.revision }}</span>
        <time :datetime="item.updatedAt">{{
          updatedLabel(item.updatedAt)
        }}</time>
      </div>
    </RouterLink>
  </div>
  <NEmpty v-else description="没有符合条件的文档" />
</template>

<style scoped>
.document-list {
  border-top: 1px solid var(--cv-border);
}

.document-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 20px;
  padding: 16px 4px;
  border-bottom: 1px solid var(--cv-border);
  color: inherit;
  text-decoration: none;
}

.document-row:hover,
.document-row:focus-visible {
  background: color-mix(in srgb, var(--cv-accent) 5%, transparent);
}

.document-title-line {
  display: flex;
  align-items: center;
  gap: 8px;
}

.document-title-line strong {
  overflow: hidden;
  font-size: 15px;
  font-weight: 650;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.document-main p {
  margin: 5px 0 0;
  overflow: hidden;
  color: var(--cv-muted);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.document-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
  margin-top: 8px;
}

.document-tags span {
  padding: 2px 6px;
  border: 1px solid var(--cv-border);
  border-radius: 4px;
  color: var(--cv-muted);
  font-size: 11px;
}

.document-meta {
  display: grid;
  justify-items: end;
  align-content: start;
  gap: 6px;
  color: var(--cv-muted);
  font-size: 11px;
  white-space: nowrap;
}

@media (max-width: 560px) {
  .document-row {
    grid-template-columns: 1fr;
    gap: 10px;
  }

  .document-meta {
    grid-auto-flow: column;
    justify-content: start;
  }
}
</style>
