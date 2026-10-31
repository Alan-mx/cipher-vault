<script setup lang="ts">
import { computed } from "vue";
import { NButton, NCheckbox, NIcon, NSelect, NSpin, NTag } from "naive-ui";
import { Folder, Plus, Tags } from "@lucide/vue";
import { useCatalogStore } from "../../stores/catalog";
import type { VaultFormat } from "../../types/domain";

const catalog = useCatalogStore();
const folderOptions = computed(() =>
  catalog.folders.map((folder) => ({ label: folder, value: folder })),
);
const formatOptions = [
  { label: "Markdown", value: "md" },
  { label: "纯文本", value: "txt" },
] satisfies Array<{ label: string; value: VaultFormat }>;

const updateFolder = async (value: string | null): Promise<void> => {
  await catalog.setFilters({ folder: value });
};

const updateFormat = async (value: VaultFormat | null): Promise<void> => {
  await catalog.setFilters({ format: value });
};

const updateOrphanOnly = async (value: boolean): Promise<void> => {
  await catalog.setFilters({ orphanOnly: value });
};
</script>

<template>
  <aside class="catalog-sidebar" aria-label="文档筛选">
    <div class="sidebar-heading">
      <div>
        <p>知识库</p>
        <strong>{{ catalog.items.length }} 篇已加载</strong>
      </div>
      <NButton size="small" secondary>
        <template #icon>
          <NIcon :component="Plus" aria-hidden="true" />
        </template>
        新建
      </NButton>
    </div>

    <section class="filter-section">
      <label class="filter-label" for="catalog-folder">
        <NIcon :component="Folder" size="15" aria-hidden="true" />
        文件夹
      </label>
      <NSelect
        id="catalog-folder"
        aria-label="文件夹筛选"
        :input-props="{ 'aria-label': '文件夹筛选' }"
        :value="catalog.filters.folder"
        :options="folderOptions"
        clearable
        filterable
        placeholder="全部文件夹"
        @update:value="updateFolder"
      />
    </section>

    <section class="filter-section">
      <label class="filter-label">格式</label>
      <NSelect
        aria-label="格式筛选"
        :input-props="{ 'aria-label': '格式筛选' }"
        :value="catalog.filters.format"
        :options="formatOptions"
        clearable
        placeholder="全部格式"
        @update:value="updateFormat"
      />
    </section>

    <section class="filter-section">
      <div class="filter-label-row">
        <span class="filter-label">
          <NIcon :component="Tags" size="15" aria-hidden="true" />
          标签
        </span>
        <NButton
          text
          size="tiny"
          :loading="catalog.loadingTags"
          @click="catalog.loadTags()"
        >
          刷新
        </NButton>
      </div>
      <div v-if="catalog.tags.length" class="tag-cloud">
        <button
          v-for="tag in catalog.tags"
          :key="tag.name"
          class="tag-button"
          :class="{
            'tag-button--active': catalog.filters.tags.includes(tag.name),
          }"
          type="button"
          @click="catalog.toggleTag(tag.name)"
        >
          <NTag
            size="small"
            :type="
              catalog.filters.tags.includes(tag.name) ? 'primary' : 'default'
            "
            :bordered="false"
          >
            {{ tag.name }} · {{ tag.documentCount }}
          </NTag>
        </button>
      </div>
      <NSpin v-else-if="catalog.loadingTags" size="small" />
      <p v-else class="empty-hint">当前没有标签。</p>
    </section>

    <section class="filter-section orphan-filter">
      <NCheckbox
        :checked="catalog.filters.orphanOnly"
        @update:checked="updateOrphanOnly"
      >
        只看孤立文档
      </NCheckbox>
      <p>没有来自其他文档的有效反向链接。</p>
    </section>

    <p class="search-entry">搜索在工作区右上角可用。</p>
  </aside>
</template>

<style scoped>
.catalog-sidebar {
  display: flex;
  width: 300px;
  min-width: 280px;
  flex-direction: column;
  gap: 24px;
  padding: 20px;
  border-right: 1px solid var(--cv-border);
  background: color-mix(in srgb, var(--cv-surface) 75%, transparent);
  overflow: auto;
}

.sidebar-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.sidebar-heading p,
.sidebar-heading strong {
  display: block;
  margin: 0;
}

.sidebar-heading p {
  color: var(--cv-muted);
  font-size: 12px;
}

.sidebar-heading strong {
  margin-top: 3px;
  font-size: 14px;
}

.filter-section {
  display: grid;
  gap: 8px;
}

.filter-label,
.filter-label-row {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--cv-muted);
  font-size: 12px;
  font-weight: 650;
}

.filter-label-row {
  justify-content: space-between;
}

.tag-cloud {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.tag-button {
  padding: 0;
  border: 0;
  background: transparent;
}

.empty-hint,
.orphan-filter p {
  margin: 0;
  color: var(--cv-muted);
  font-size: 12px;
  line-height: 1.55;
}

.search-entry {
  margin-top: auto;
  color: var(--cv-muted);
  font-size: 12px;
  line-height: 1.5;
}

@media (max-width: 1099px) {
  .catalog-sidebar {
    width: 260px;
    min-width: 240px;
  }
}

@media (max-width: 899px) {
  .catalog-sidebar {
    width: 100%;
    min-width: 0;
    border-right: 0;
    border-bottom: 1px solid var(--cv-border);
  }
}
</style>
