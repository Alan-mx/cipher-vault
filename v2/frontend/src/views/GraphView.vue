<script setup lang="ts">
import { computed, onMounted } from "vue";
import { useRouter } from "vue-router";
import {
  NAlert,
  NButton,
  NInput,
  NInputNumber,
  NSelect,
  NSpin,
} from "naive-ui";
import GraphCanvas from "../components/graph/GraphCanvas.vue";
import { useCatalogStore } from "../stores/catalog";
import { useGraphStore } from "../stores/graph";

const catalog = useCatalogStore();
const graph = useGraphStore();
const router = useRouter();

const centerOptions = computed(() =>
  catalog.items.map((item) => ({
    label: item.title,
    value: item.documentId,
  })),
);
const tagOptions = computed(() =>
  catalog.tags.map((tag) => ({ label: tag.name, value: tag.name })),
);

onMounted(async () => {
  if (!catalog.items.length) {
    await catalog.load(false);
  }
  if (!catalog.tags.length) {
    await catalog.loadTags();
  }
});

const openDocument = async (documentId: string): Promise<void> => {
  await router.push({ name: "document", params: { id: documentId } });
};
</script>

<template>
  <section class="graph-view">
    <header class="page-heading">
      <div>
        <p>知识网络</p>
        <h1>局部关系图</h1>
      </div>
      <NButton
        type="primary"
        :disabled="!graph.centerDocumentId"
        :loading="graph.loading"
        @click="graph.load()"
      >
        更新图谱
      </NButton>
    </header>

    <div class="graph-controls">
      <NSelect
        :input-props="{ 'aria-label': '中心文档' }"
        :value="graph.centerDocumentId"
        :options="centerOptions"
        filterable
        placeholder="选择中心文档"
        @update:value="graph.setCenter($event as string)"
      />
      <NSelect
        :input-props="{ 'aria-label': '关系深度' }"
        :value="graph.filters.depth"
        :options="[
          { label: '1 跳', value: 1 },
          { label: '2 跳', value: 2 },
          { label: '3 跳', value: 3 },
        ]"
        @update:value="graph.setFilters({ depth: $event as 1 | 2 | 3 })"
      />
      <NSelect
        :input-props="{ 'aria-label': '图谱标签过滤' }"
        :value="graph.filters.tags"
        :options="tagOptions"
        multiple
        filterable
        placeholder="标签 AND"
        @update:value="graph.setFilters({ tags: $event as string[] })"
      />
      <NInput
        :value="graph.filters.folder"
        placeholder="文件夹过滤"
        @update:value="graph.setFilters({ folder: $event })"
      />
      <NInputNumber
        :input-props="{ 'aria-label': '最大节点数' }"
        :value="graph.filters.maxNodes"
        :min="1"
        :max="500"
        placeholder="节点上限"
        @update:value="graph.setFilters({ maxNodes: $event ?? 200 })"
      />
      <NInputNumber
        :input-props="{ 'aria-label': '最大边数' }"
        :value="graph.filters.maxEdges"
        :min="1"
        :max="1000"
        placeholder="边上限"
        @update:value="graph.setFilters({ maxEdges: $event ?? 400 })"
      />
    </div>

    <NAlert v-if="graph.error" type="error" :show-icon="false">
      {{ graph.error.message }}
    </NAlert>
    <NAlert v-if="graph.truncated" type="warning" :show-icon="false">
      图谱超过节点或边上限，当前结果已截断。
    </NAlert>

    <div v-if="graph.loading" class="loading">
      <NSpin size="small" />
      <span>正在计算关系图…</span>
    </div>
    <GraphCanvas
      v-else-if="graph.nodes.length"
      :nodes="graph.nodes"
      :edges="graph.edges"
      @open="openDocument"
    />
    <div v-else class="graph-empty">选择中心文档后加载 1～3 跳关系。</div>
  </section>
</template>

<style scoped>
.graph-view {
  width: min(100%, 1400px);
  padding: 32px 40px 80px;
}

.page-heading {
  display: flex;
  align-items: end;
  justify-content: space-between;
  gap: 16px;
}

.page-heading p {
  margin: 0 0 5px;
  color: var(--cv-muted);
  font-size: 12px;
}

.page-heading h1 {
  margin: 0;
  font-size: 2rem;
  letter-spacing: 0;
}

.graph-controls {
  display: grid;
  grid-template-columns:
    minmax(220px, 1.5fr) repeat(3, minmax(120px, 1fr))
    repeat(2, 110px);
  gap: 10px;
  margin: 24px 0 16px;
}

.loading {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 48px 0;
  color: var(--cv-muted);
}

.graph-empty {
  display: grid;
  min-height: 520px;
  border: 1px dashed var(--cv-border);
  border-radius: 8px;
  color: var(--cv-muted);
  place-items: center;
}

@media (max-width: 1099px) {
  .graph-controls {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 699px) {
  .graph-view {
    padding: 24px 20px 64px;
  }

  .page-heading {
    align-items: start;
    flex-direction: column;
  }

  .graph-controls {
    grid-template-columns: 1fr;
  }
}
</style>
