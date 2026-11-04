<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import {
  NAlert,
  NButton,
  NEmpty,
  NIcon,
  NSpin,
  NSwitch,
  NTabs,
  NTabPane,
} from "naive-ui";
import { RefreshCw } from "@lucide/vue";
import DocumentList from "../components/document/DocumentList.vue";
import KnowledgeFacetList from "../components/knowledge/KnowledgeFacetList.vue";
import { useKnowledgeViewStore } from "../stores/knowledge-view";
import type { KnowledgeFacetItem, KnowledgeViewField } from "../types/api";

interface ViewOption {
  value: "projects" | "types" | "maturity";
  label: string;
  field: KnowledgeViewField;
}

interface QueryValue {
  present: boolean;
  valid: boolean;
  value: string | null;
}

const views: ViewOption[] = [
  { value: "projects", label: "项目", field: "project" },
  { value: "types", label: "类型", field: "type" },
  { value: "maturity", label: "成熟度", field: "maturity" },
];

const route = useRoute();
const router = useRouter();
const store = useKnowledgeViewStore();
const loadedScope = ref<string | null>(null);
const selectionMissing = ref(false);
const routeError = ref<string | null>(null);
let syncGeneration = 0;

const readQuery = (key: string): QueryValue => {
  const value = route.query[key];
  if (value === undefined) {
    return { present: false, valid: true, value: null };
  }
  if (typeof value !== "string") {
    return { present: true, valid: false, value: null };
  }
  return { present: true, valid: true, value };
};

const viewQuery = computed(() => readQuery("view"));
const valueQuery = computed(() => readQuery("value"));
const unsetQuery = computed(() => readQuery("unset"));
const activeOnlyQuery = computed(() => readQuery("activeOnly"));
const view = computed<ViewOption>(
  () => views.find((item) => item.value === viewQuery.value.value) ?? views[0]!,
);
const viewIsValid = computed(
  () =>
    !viewQuery.value.present ||
    views.some((item) => item.value === viewQuery.value.value),
);
const activeOnlyValueValid = computed(
  () =>
    !activeOnlyQuery.value.present ||
    activeOnlyQuery.value.value === "true" ||
    activeOnlyQuery.value.value === "false",
);
const activeOnly = computed(
  () =>
    view.value.value === "projects" && activeOnlyQuery.value.value === "true",
);
const activeOnlyForWrongView = computed(
  () => activeOnlyQuery.value.present && view.value.value !== "projects",
);
const unsetValid = computed(
  () => !unsetQuery.value.present || unsetQuery.value.value === "true",
);
const selectedUnset = computed(() => unsetQuery.value.value === "true");
const selectedKey = computed(() =>
  valueQuery.value.present ? valueQuery.value.value : null,
);
const selectionValid = computed(() => {
  if (
    !valueQuery.value.valid ||
    !unsetQuery.value.valid ||
    !activeOnlyQuery.value.valid
  ) {
    return false;
  }
  if (valueQuery.value.present && unsetQuery.value.present) {
    return false;
  }
  if (valueQuery.value.present && !valueQuery.value.value?.trim()) {
    return false;
  }
  return unsetValid.value;
});
const hasSelection = computed(
  () => valueQuery.value.present || selectedUnset.value,
);
const currentScope = computed(() => `${view.value.field}:${activeOnly.value}`);
const selectedFacet = computed(() =>
  selectedUnset.value
    ? (store.facets.find((item) => item.isUnset) ?? null)
    : (store.facets.find((item) => item.key === selectedKey.value) ?? null),
);
const currentSelectionLabel = computed(() =>
  selectedUnset.value
    ? "未设置"
    : (selectedFacet.value?.label ?? selectedKey.value ?? ""),
);
const routeIsValid = computed(
  () =>
    viewIsValid.value &&
    activeOnlyValueValid.value &&
    !activeOnlyForWrongView.value &&
    selectionValid.value,
);

const queryFor = (
  targetView: ViewOption,
  targetKey: string | null,
  targetUnset: boolean,
  targetActiveOnly: boolean,
) => ({
  view: targetView.value,
  ...(targetUnset ? { unset: "true" } : targetKey ? { value: targetKey } : {}),
  ...(targetActiveOnly ? { activeOnly: "true" } : {}),
});

const updateRoute = async (query: Record<string, string>): Promise<void> => {
  await router.push({ name: "knowledge-views", query });
};

const setView = async (name: string | number): Promise<void> => {
  const target = views.find((item) => item.value === name);
  if (!target || target.value === view.value.value) {
    return;
  }
  await updateRoute(queryFor(target, null, false, false));
};

const selectFacet = async (item: KnowledgeFacetItem): Promise<void> => {
  await updateRoute(
    queryFor(
      view.value,
      item.isUnset ? null : item.key,
      item.isUnset,
      activeOnly.value,
    ),
  );
};

const clearSelection = async (): Promise<void> => {
  await updateRoute(queryFor(view.value, null, false, activeOnly.value));
};

const setActiveOnly = async (value: boolean): Promise<void> => {
  if (view.value.value !== "projects") {
    return;
  }
  await updateRoute(
    queryFor(view.value, selectedKey.value, selectedUnset.value, value),
  );
};

const loadSelectedDocuments = async (
  generation: number,
  append = false,
): Promise<void> => {
  if (!hasSelection.value) {
    store.clearDocuments();
    selectionMissing.value = false;
    return;
  }
  if (!selectedUnset.value && !selectedFacet.value && !store.facetsTruncated) {
    store.clearDocuments();
    selectionMissing.value = true;
    return;
  }

  const success = await store.loadDocuments(
    view.value.field,
    {
      facetKey: selectedKey.value ?? undefined,
      unset: selectedUnset.value,
    },
    activeOnly.value,
    append,
  );
  if (generation !== syncGeneration || !success) {
    return;
  }
  selectionMissing.value = !selectedUnset.value && store.documents.length === 0;
};

const syncRoute = async (): Promise<void> => {
  const generation = ++syncGeneration;
  selectionMissing.value = false;
  routeError.value = !viewIsValid.value
    ? "视图不存在"
    : !activeOnlyValueValid.value ||
        activeOnlyForWrongView.value ||
        !selectionValid.value
      ? "视图参数无效"
      : null;
  if (routeError.value) {
    store.clearDocuments();
    return;
  }

  if (loadedScope.value !== currentScope.value) {
    store.clearDocuments();
    const loaded = await store.loadFacets(view.value.field, activeOnly.value);
    if (generation !== syncGeneration || !loaded) {
      return;
    }
    loadedScope.value = currentScope.value;
  }

  await loadSelectedDocuments(generation);
};

const refresh = async (): Promise<void> => {
  if (!routeIsValid.value) {
    return;
  }
  loadedScope.value = null;
  await syncRoute();
};

const retryDocuments = (): Promise<void> =>
  loadSelectedDocuments(syncGeneration);

const loadMoreDocuments = (): Promise<void> =>
  loadSelectedDocuments(syncGeneration, true);

watch(
  () => route.fullPath,
  () => void syncRoute(),
);
onMounted(() => void syncRoute());
</script>

<template>
  <section class="knowledge-views">
    <header class="views-heading">
      <div>
        <p>Frontmatter 知识组织</p>
        <h1>知识视图</h1>
      </div>
      <NButton
        quaternary
        circle
        aria-label="刷新知识视图"
        :loading="store.facetsLoading || store.documentsLoading"
        @click="refresh"
      >
        <template #icon>
          <NIcon :component="RefreshCw" aria-hidden="true" />
        </template>
      </NButton>
    </header>

    <NTabs :value="view.value" type="line" animated @update:value="setView">
      <NTabPane
        v-for="item in views"
        :key="item.value"
        :name="item.value"
        :tab="item.label"
      >
        <span />
      </NTabPane>
    </NTabs>

    <NAlert
      v-if="routeError"
      type="error"
      :show-icon="false"
      class="view-error"
    >
      {{ routeError }}
      <NButton text type="primary" @click="updateRoute({ view: 'projects' })">
        返回项目视图
      </NButton>
    </NAlert>

    <div v-else class="views-layout">
      <aside class="facets-panel" aria-label="知识视图分类">
        <div class="facets-heading">
          <strong>{{ view.label }}</strong>
          <label v-if="view.value === 'projects'" class="active-only-filter">
            <span>仅进行中</span>
            <NSwitch
              :value="activeOnly"
              size="small"
              aria-label="仅显示进行中项目"
              @update:value="setActiveOnly"
            />
          </label>
        </div>
        <NAlert v-if="store.facetsError" type="error" :show-icon="false">
          <p class="inline-error">{{ store.facetsError.message }}</p>
          <NButton size="tiny" secondary @click="refresh">重试</NButton>
        </NAlert>
        <KnowledgeFacetList
          v-else
          :items="store.facets"
          :selected-key="selectedKey"
          :selected-unset="selectedUnset"
          :loading="store.facetsLoading"
          :truncated="store.facetsTruncated"
          @select="selectFacet"
        />
      </aside>

      <section class="documents-panel">
        <template v-if="hasSelection">
          <header class="documents-heading">
            <div>
              <p>分类文档</p>
              <h2>{{ currentSelectionLabel }}</h2>
            </div>
            <NButton text size="small" @click="clearSelection">
              返回全部分类
            </NButton>
          </header>

          <NAlert v-if="selectionMissing" type="warning" :show-icon="false">
            分类已不存在，可能已被删除或不再匹配当前筛选。
          </NAlert>
          <NAlert
            v-else-if="store.documentsError"
            type="error"
            :show-icon="false"
          >
            <p class="inline-error">{{ store.documentsError.message }}</p>
            <NButton size="tiny" secondary @click="retryDocuments">
              重试
            </NButton>
          </NAlert>
          <div
            v-else-if="store.documentsLoading && !store.documents.length"
            class="documents-loading"
          >
            <NSpin size="small" />
            <span>正在加载文档…</span>
          </div>
          <DocumentList v-else :items="store.documents" />
          <div
            v-if="store.documentsNextCursor && !selectionMissing"
            class="load-more"
          >
            <NButton
              secondary
              :loading="store.documentsLoading"
              @click="loadMoreDocuments"
            >
              加载更多
            </NButton>
          </div>
        </template>
        <NEmpty v-else description="选择左侧分类查看文档" />
      </section>
    </div>
  </section>
</template>

<style scoped>
.knowledge-views {
  width: min(100%, 1280px);
  margin: 0 auto;
  padding: 32px 40px 80px;
}

.views-heading,
.facets-heading,
.documents-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}

.views-heading {
  margin-bottom: 14px;
}

.views-heading p,
.views-heading h1,
.documents-heading p,
.documents-heading h2 {
  margin: 0;
}

.views-heading p,
.documents-heading p {
  color: var(--cv-muted);
  font-size: 12px;
}

.views-heading h1 {
  margin-top: 4px;
  font-size: 2rem;
}

.view-error {
  margin-top: 16px;
}

.views-layout {
  display: grid;
  grid-template-columns: minmax(230px, 300px) minmax(0, 1fr);
  gap: 24px;
  margin-top: 12px;
}

.facets-panel,
.documents-panel {
  min-width: 0;
  padding: 16px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
}

.facets-panel {
  align-self: start;
  max-height: calc(100vh - 250px);
  overflow: auto;
}

.facets-heading {
  margin-bottom: 12px;
}

.facets-heading strong {
  font-size: 13px;
}

.active-only-filter {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--cv-muted);
  font-size: 11px;
}

.documents-panel {
  min-height: 320px;
}

.documents-heading {
  align-items: end;
  margin-bottom: 16px;
}

.documents-heading h2 {
  margin-top: 4px;
  font-size: 1.25rem;
}

.documents-loading {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 48px 0;
  color: var(--cv-muted);
}

.inline-error {
  margin: 0 0 8px;
  font-size: 12px;
}

.load-more {
  display: flex;
  justify-content: center;
  padding-top: 18px;
}

@media (max-width: 899px) {
  .knowledge-views {
    padding: 24px 20px 64px;
  }

  .views-layout {
    grid-template-columns: 1fr;
  }

  .facets-panel {
    max-height: 300px;
  }
}

@media (max-width: 560px) {
  .views-heading h1 {
    font-size: 1.6rem;
  }
}
</style>
