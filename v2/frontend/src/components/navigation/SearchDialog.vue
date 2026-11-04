<script setup lang="ts">
import { computed, nextTick, ref, watch } from "vue";
import {
  NAlert,
  NButton,
  NCheckbox,
  NDatePicker,
  NIcon,
  NInput,
  NModal,
  NRadioButton,
  NRadioGroup,
  NSelect,
  NSpin,
  NTag,
} from "naive-ui";
import { Plus, Trash2, X } from "@lucide/vue";
import { useCatalogStore } from "../../stores/catalog";
import {
  useSearchStore,
  type SearchPropertyFilterDraft,
  type SearchPropertyValueType,
} from "../../stores/search";
import type { VaultFormat } from "../../types/domain";
import type { SearchMode, SearchPropertyOperator } from "../../types/api";

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
const operatorOptions = [
  { label: "存在", value: "exists" },
  { label: "等于", value: "eq" },
  { label: "包含", value: "contains" },
  { label: "属于", value: "in" },
  { label: "大于等于", value: "gte" },
  { label: "小于等于", value: "lte" },
] satisfies Array<{ label: string; value: SearchPropertyOperator }>;
const valueTypeOptions = [
  { label: "字符串", value: "string" },
  { label: "数字", value: "number" },
  { label: "布尔", value: "boolean" },
  { label: "null", value: "null" },
] satisfies Array<{ label: string; value: SearchPropertyValueType }>;
const modeOptions = [
  { label: "关键词", value: "keyword" },
  { label: "语义", value: "semantic" },
  { label: "混合", value: "hybrid" },
] satisfies Array<{ label: string; value: SearchMode }>;
const numericTypeOptions = valueTypeOptions.filter(
  (option) => option.value === "string" || option.value === "number",
);
const booleanOptions = [
  { label: "true", value: "true" },
  { label: "false", value: "false" },
];
const degradedReasonLabel = computed(() => {
  switch (search.degradedReason) {
    case "not_configured":
      return "未配置语义索引";
    case "remote_consent_required":
      return "远程语义索引尚未确认";
    case "index_building":
      return "语义索引构建中";
    case "profile_invalid":
      return "语义 Profile 已失效";
    case "index_failed":
      return "语义索引失败";
    case "provider_timeout":
      return "Embedding 服务超时";
    case "provider_rate_limited":
      return "Embedding 服务限流";
    case "provider_invalid_response":
      return "Embedding 响应无效";
    case "provider_unavailable":
      return "Embedding 服务不可用";
    default:
      return search.degradedReason ?? "语义检索不可用";
  }
});

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

const updateOperator = (
  filter: SearchPropertyFilterDraft,
  operator: SearchPropertyOperator,
): void => {
  const valueType =
    operator === "exists"
      ? "boolean"
      : (operator === "gte" || operator === "lte") &&
          filter.valueType !== "number"
        ? "string"
        : filter.valueType;
  search.updatePropertyFilter(filter.id, { operator, valueType });
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
  () => [search.query, search.searchMode, search.filters] as const,
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

      <div class="search-mode-row">
        <span>检索模式</span>
        <NRadioGroup
          :value="search.searchMode"
          size="small"
          @update:value="search.setMode($event as SearchMode)"
        >
          <NRadioButton
            v-for="option in modeOptions"
            :key="option.value"
            :value="option.value"
          >
            {{ option.label }}
          </NRadioButton>
        </NRadioGroup>
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

      <section class="property-filters">
        <div class="property-heading">
          <strong>属性条件</strong>
          <NRadioGroup
            v-if="search.filters.propertyFilters.length"
            :value="search.filters.propertyMatch"
            size="small"
            @update:value="search.setPropertyMatch($event as 'all' | 'any')"
          >
            <NRadioButton value="all">全部满足</NRadioButton>
            <NRadioButton value="any">任一满足</NRadioButton>
          </NRadioGroup>
        </div>
        <div
          v-for="filter in search.filters.propertyFilters"
          :key="filter.id"
          class="property-row"
        >
          <NInput
            :value="filter.field"
            size="small"
            placeholder="例如 status / source.kind"
            :input-props="{ 'aria-label': '属性字段' }"
            @update:value="
              search.updatePropertyFilter(filter.id, { field: $event })
            "
          />
          <NSelect
            :value="filter.operator"
            :options="operatorOptions"
            size="small"
            :input-props="{ 'aria-label': '属性操作符' }"
            @update:value="
              updateOperator(filter, $event as SearchPropertyOperator)
            "
          />
          <template v-if="filter.operator === 'exists'">
            <NSelect
              :value="String(filter.booleanValue)"
              :options="booleanOptions"
              size="small"
              :input-props="{ 'aria-label': '属性是否存在' }"
              @update:value="
                search.updatePropertyFilter(filter.id, {
                  booleanValue: $event === 'true',
                })
              "
            />
          </template>
          <template v-else>
            <NSelect
              :value="filter.valueType"
              :options="
                filter.operator === 'gte' || filter.operator === 'lte'
                  ? numericTypeOptions
                  : valueTypeOptions
              "
              size="small"
              :input-props="{ 'aria-label': '属性值类型' }"
              @update:value="
                search.updatePropertyFilter(filter.id, {
                  valueType: $event as SearchPropertyValueType,
                })
              "
            />
            <NSelect
              v-if="filter.valueType === 'boolean'"
              :value="String(filter.booleanValue)"
              :options="booleanOptions"
              size="small"
              :input-props="{ 'aria-label': '属性布尔值' }"
              @update:value="
                search.updatePropertyFilter(filter.id, {
                  booleanValue: $event === 'true',
                })
              "
            />
            <span v-else-if="filter.valueType === 'null'" class="property-null">
              null
            </span>
            <NInput
              v-else
              :value="
                filter.operator === 'in' ? filter.valuesText : filter.value
              "
              size="small"
              :placeholder="
                filter.operator === 'in' ? '多个值用逗号分隔' : '属性值'
              "
              :input-props="{
                'aria-label':
                  filter.operator === 'in' ? '属性值列表' : '属性值',
              }"
              @update:value="
                search.updatePropertyFilter(
                  filter.id,
                  filter.operator === 'in'
                    ? { valuesText: $event }
                    : { value: $event },
                )
              "
            />
          </template>
          <NButton
            quaternary
            circle
            aria-label="删除属性条件"
            @click="search.removePropertyFilter(filter.id)"
          >
            <template #icon>
              <NIcon :component="Trash2" aria-hidden="true" />
            </template>
          </NButton>
        </div>
        <NButton
          text
          size="small"
          :disabled="search.filters.propertyFilters.length >= 16"
          @click="search.addPropertyFilter()"
        >
          <template #icon>
            <NIcon :component="Plus" aria-hidden="true" />
          </template>
          添加属性条件
        </NButton>
      </section>

      <NAlert
        v-if="search.error"
        class="search-status"
        type="error"
        :show-icon="false"
      >
        {{ search.error.message }}
      </NAlert>

      <NAlert
        v-else-if="search.degraded"
        class="search-status"
        type="warning"
        :show-icon="false"
      >
        语义检索暂不可用，已返回关键词结果。
        <span class="degraded-reason">
          {{ degradedReasonLabel }}（<code>{{ search.degradedReason }}</code
          >）
        </span>
      </NAlert>

      <div class="search-results">
        <div
          v-if="search.loading && !search.hits.length"
          class="search-loading"
        >
          <NSpin size="small" />
          <span>正在搜索…</span>
        </div>
        <p v-else-if="!search.canSearch" class="search-empty">
          输入关键词或至少一个过滤条件后开始搜索。
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
            <div class="result-heading">
              <strong>{{ hit.title }}</strong>
              <span
                v-for="source in hit.retrievalSources"
                :key="source"
                class="source-label"
              >
                {{ source === "semantic" ? "语义" : "关键词" }}
              </span>
            </div>
            <p>{{ hit.semanticMatch?.excerpt ?? hit.snippet }}</p>
            <div
              v-if="hit.semanticMatch?.headingPath.length"
              class="result-path"
            >
              {{ hit.semanticMatch.headingPath.join(" / ") }}
            </div>
            <div class="result-meta">
              <NTag size="tiny" :bordered="false"> R{{ hit.revision }} </NTag>
              <span
                v-if="hit.fusionScore !== null && hit.fusionScore !== undefined"
              >
                融合分 {{ hit.fusionScore.toFixed(4) }}
              </span>
              <span
                v-else-if="
                  hit.semanticMatch?.cosineScore !== null &&
                  hit.semanticMatch?.cosineScore !== undefined
                "
              >
                相似度
                {{ hit.semanticMatch.cosineScore.toFixed(3) }}（非置信度）
              </span>
              <span v-else>
                {{
                  search.query.trim()
                    ? `score ${hit.score.toFixed(2)}`
                    : "属性筛选"
                }}
              </span>
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

.search-mode-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-top: 12px;
  color: var(--cv-muted);
  font-size: 12px;
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

.property-filters {
  display: grid;
  gap: 8px;
  margin: 0 0 12px;
  padding-top: 12px;
  border-top: 1px solid var(--cv-border);
}

.property-heading {
  display: flex;
  min-height: 28px;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  font-size: 12px;
}

.property-row {
  display: grid;
  grid-template-columns:
    minmax(150px, 1.4fr) minmax(100px, 0.8fr)
    minmax(90px, 0.7fr) minmax(130px, 1.2fr) 32px;
  align-items: center;
  gap: 6px;
}

.property-null {
  display: flex;
  height: 28px;
  align-items: center;
  padding: 0 10px;
  border: 1px solid var(--cv-border);
  border-radius: 4px;
  color: var(--cv-muted);
  font-size: 12px;
}

.search-status {
  margin-bottom: 12px;
}

.degraded-reason {
  margin-left: 4px;
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

.result-heading {
  display: flex;
  align-items: center;
  gap: 7px;
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

.source-label {
  padding: 1px 5px;
  border: 1px solid var(--cv-border);
  border-radius: 4px;
  color: var(--cv-muted);
  font-size: 10px;
}

.result-path {
  overflow: hidden;
  margin: -2px 0 6px;
  color: var(--cv-muted);
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.result-meta {
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

  .search-mode-row {
    align-items: flex-start;
    flex-direction: column;
  }

  .property-heading {
    align-items: flex-start;
    flex-direction: column;
  }

  .property-row {
    grid-template-columns: 1fr 1fr;
  }

  .property-row > :nth-child(1),
  .property-row > :nth-child(4) {
    grid-column: 1 / -1;
  }
}
</style>
