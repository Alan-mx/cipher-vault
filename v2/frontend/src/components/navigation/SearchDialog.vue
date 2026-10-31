<script setup lang="ts">
import { computed, nextTick, ref, watch } from "vue";
import {
  NAlert,
  NButton,
  NCheckbox,
  NDatePicker,
  NIcon,
  NModal,
  NSelect,
  NSpin,
  NTag,
} from "naive-ui";
import { X } from "@lucide/vue";
import { useCatalogStore } from "../../stores/catalog";
import { useSearchStore } from "../../stores/search";
import type { VaultFormat } from "../../types/domain";

const props = defineProps<{
  show: boolean;
}>();

const emit = defineEmits<{
  "update:show": [value: boolean];
  open: [documentId: string];
}>();

const search = useSearchStore();
const catalog = useCatalogStore();
const debounceTimer = ref<number | null>(null);
const updatedAfter = ref<number | null>(null);
const updatedBefore = ref<number | null>(null);
const tagOptions = computed(() =>
  catalog.tags.map((tag) => ({
    label: `${tag.name} · ${tag.documentCount}`,
    value: tag.name,
  })),
);
const formatOptions = [
  { label: "Markdown", value: "md" },
  { label: "纯文本", value: "txt" },
] satisfies Array<{ label: string; value: VaultFormat }>;

const open = async (): Promise<void> => {
  await catalog.loadTags();
  await nextTick();
  document.querySelector<HTMLInputElement>(".search-dialog input")?.focus();
};

const close = (): void => {
  emit("update:show", false);
};

const updateUpdatedAfter = (value: number | null): void => {
  updatedAfter.value = value;
  search.setFilters({
    updatedAfter: value === null ? null : new Date(value).toISOString(),
  });
};

const updateUpdatedBefore = (value: number | null): void => {
  updatedBefore.value = value;
  search.setFilters({
    updatedBefore: value === null ? null : new Date(value).toISOString(),
  });
};

watch(
  () => props.show,
  (show) => {
    if (show) {
      void open();
    } else if (debounceTimer.value !== null) {
      window.clearTimeout(debounceTimer.value);
      debounceTimer.value = null;
    }
  },
);

watch(
  () => [search.query, search.filters] as const,
  () => {
    if (!props.show) {
      return;
    }
    if (debounceTimer.value !== null) {
      window.clearTimeout(debounceTimer.value);
    }
    if (!search.canSearch) {
      return;
    }
    debounceTimer.value = window.setTimeout(() => {
      debounceTimer.value = null;
      void search.search(false);
    }, 300);
  },
  { deep: true },
);
</script>

<template>
  <NModal :show="show" @update:show="emit('update:show', $event)">
    <section
      class="search-dialog"
      role="dialog"
      aria-modal="true"
      aria-label="搜索文档"
    >
      <div class="search-header">
        <input
          :value="search.query"
          class="search-input"
          placeholder="输入全文搜索词…"
          aria-label="全文搜索词"
          @input="search.setQuery(($event.target as HTMLInputElement).value)"
        />
        <NButton quaternary circle aria-label="关闭搜索" @click="close">
          <template #icon>
            <NIcon :component="X" aria-hidden="true" />
          </template>
        </NButton>
      </div>

      <div class="search-filters">
        <NSelect
          aria-label="搜索格式"
          :input-props="{ 'aria-label': '搜索格式' }"
          :value="search.filters.format"
          :options="formatOptions"
          clearable
          placeholder="格式"
          size="small"
          @update:value="
            search.setFilters({ format: $event as VaultFormat | null })
          "
        />
        <NSelect
          aria-label="搜索标签"
          :input-props="{ 'aria-label': '搜索标签' }"
          :value="search.filters.tags"
          :options="tagOptions"
          clearable
          multiple
          filterable
          placeholder="标签 AND"
          size="small"
          @update:value="search.setFilters({ tags: $event as string[] })"
        />
        <NDatePicker
          aria-label="更新晚于"
          :input-props="{ 'aria-label': '更新晚于' }"
          :value="updatedAfter"
          type="datetime"
          clearable
          placeholder="更新晚于"
          size="small"
          @update:value="updateUpdatedAfter"
        />
        <NDatePicker
          aria-label="更新早于"
          :input-props="{ 'aria-label': '更新早于' }"
          :value="updatedBefore"
          type="datetime"
          clearable
          placeholder="更新早于"
          size="small"
          @update:value="updateUpdatedBefore"
        />
        <NCheckbox
          :checked="search.filters.hasBacklinks"
          @update:checked="search.setFilters({ hasBacklinks: $event })"
        >
          有关联
        </NCheckbox>
        <NCheckbox
          :checked="search.filters.orphanOnly"
          @update:checked="search.setFilters({ orphanOnly: $event })"
        >
          孤立
        </NCheckbox>
      </div>

      <NAlert
        v-if="search.error"
        class="search-status"
        type="error"
        :show-icon="false"
      >
        {{ search.error.message }}
      </NAlert>

      <div class="search-results">
        <div
          v-if="search.loading && !search.hits.length"
          class="search-loading"
        >
          <NSpin size="small" />
          <span>正在搜索…</span>
        </div>
        <p v-else-if="!search.query.trim()" class="search-empty">
          输入关键词后开始搜索。查询词只保留在当前页面内存中。
        </p>
        <p
          v-else-if="!search.hits.length && !search.loading"
          class="search-empty"
        >
          没有匹配文档。
        </p>
        <div v-else class="search-result-list">
          <button
            v-for="hit in search.hits"
            :key="hit.documentId"
            type="button"
            @click="emit('open', hit.documentId)"
          >
            <strong>{{ hit.title }}</strong>
            <p>{{ hit.snippet }}</p>
            <div>
              <NTag size="tiny" :bordered="false"> R{{ hit.revision }} </NTag>
              <span>score {{ hit.score.toFixed(2) }}</span>
            </div>
          </button>
        </div>
      </div>

      <footer v-if="search.coverage" class="search-footer">
        <span>
          {{ search.coverage.matchedDocuments }} /
          {{ search.coverage.totalActiveDocuments }} 篇匹配
        </span>
        <span v-if="search.coverage.truncated" class="coverage-truncated">
          扫描已被截断
        </span>
        <NButton
          v-if="search.nextCursor"
          size="tiny"
          secondary
          :loading="search.loading"
          @click="search.search(true)"
        >
          加载更多
        </NButton>
      </footer>
    </section>
  </NModal>
</template>

<style scoped>
.search-dialog {
  display: grid;
  width: min(94vw, 820px);
  max-height: min(84vh, 760px);
  grid-template-rows: auto auto auto 1fr auto;
  padding: 20px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}

.search-header {
  display: flex;
  gap: 8px;
}

.search-input {
  width: 100%;
  height: 40px;
  padding: 0 12px;
  border: 1px solid var(--cv-border);
  border-radius: 4px;
  background: var(--cv-surface);
  color: var(--cv-text);
  outline: none;
}

.search-input:focus {
  border-color: var(--cv-accent);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--cv-accent) 20%, transparent);
}

.search-filters {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px;
  margin: 12px 0;
}

.search-status {
  margin-bottom: 12px;
}

.search-results {
  min-height: 180px;
  overflow: auto;
}

.search-loading,
.search-empty {
  display: grid;
  gap: 10px;
  color: var(--cv-muted);
  place-items: center;
}

.search-result-list {
  display: grid;
  gap: 2px;
}

.search-result-list button {
  padding: 12px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: inherit;
  text-align: left;
}

.search-result-list button:hover,
.search-result-list button:focus-visible {
  background: color-mix(in srgb, var(--cv-accent) 7%, transparent);
}

.search-result-list strong {
  font-size: 14px;
}

.search-result-list p {
  display: -webkit-box;
  overflow: hidden;
  margin: 6px 0;
  color: var(--cv-muted);
  font-size: 12px;
  line-height: 1.6;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}

.search-result-list div {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--cv-muted);
  font-size: 11px;
}

.search-footer {
  display: flex;
  align-items: center;
  gap: 10px;
  padding-top: 12px;
  border-top: 1px solid var(--cv-border);
  color: var(--cv-muted);
  font-size: 12px;
}

.coverage-truncated {
  color: var(--cv-warning);
}

@media (max-width: 699px) {
  .search-filters {
    grid-template-columns: 1fr;
  }
}
</style>
