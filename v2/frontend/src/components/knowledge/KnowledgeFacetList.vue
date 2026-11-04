<script setup lang="ts">
import { NEmpty, NSpin, NTag } from "naive-ui";
import type { KnowledgeFacetItem } from "../../types/api";

defineProps<{
  items: KnowledgeFacetItem[];
  selectedKey: string | null;
  selectedUnset: boolean;
  loading: boolean;
  truncated: boolean;
}>();

const emit = defineEmits<{
  select: [item: KnowledgeFacetItem];
}>();

const isSelected = (
  item: KnowledgeFacetItem,
  selectedKey: string | null,
  selectedUnset: boolean,
): boolean =>
  item.isUnset ? selectedUnset : !selectedUnset && item.key === selectedKey;
</script>

<template>
  <div class="facet-list" :aria-busy="loading">
    <div v-if="loading" class="facet-loading">
      <NSpin size="small" />
      <span>正在加载分类…</span>
    </div>
    <template v-else-if="items.length">
      <button
        v-for="item in items"
        :key="item.isUnset ? '__unset__' : (item.key ?? item.label)"
        type="button"
        :class="{
          'facet-item--active': isSelected(item, selectedKey, selectedUnset),
        }"
        @click="emit('select', item)"
      >
        <span class="facet-label">{{ item.label }}</span>
        <NTag size="tiny" :bordered="false">{{ item.documentCount }}</NTag>
      </button>
      <p v-if="truncated" class="facet-truncated">
        分类列表已截断，仅显示文档数最多的 200 项。
      </p>
    </template>
    <NEmpty v-else description="当前没有可展示的分类" size="small" />
  </div>
</template>

<style scoped>
.facet-list {
  display: grid;
  gap: 4px;
}

.facet-list button {
  display: flex;
  width: 100%;
  min-width: 0;
  min-height: 38px;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 7px 9px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: inherit;
  text-align: left;
}

.facet-list button:hover,
.facet-list button:focus-visible {
  background: color-mix(in srgb, var(--cv-accent) 7%, transparent);
}

.facet-list button.facet-item--active {
  background: color-mix(in srgb, var(--cv-accent) 13%, transparent);
  color: var(--cv-accent);
}

.facet-label {
  overflow: hidden;
  font-size: 13px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.facet-loading {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 16px 4px;
  color: var(--cv-muted);
  font-size: 12px;
}

.facet-truncated {
  margin: 8px 4px 0;
  color: var(--cv-muted);
  font-size: 11px;
  line-height: 1.5;
}
</style>
