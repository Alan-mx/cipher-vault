<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import {
  NAlert,
  NButton,
  NCheckbox,
  NIcon,
  NInput,
  NInputNumber,
  NSelect,
  NSpin,
} from "naive-ui";
import { Plus, Save } from "@lucide/vue";
import { knowledgeBaseApi } from "../api/knowledge-base";
import type {
  KnowledgeModel,
  KnowledgeProperty,
  KnowledgeTemplate,
  KnowledgeType,
  RelationType,
  SourceRule,
  Workflow,
} from "../types/knowledge-base";
import { describeApiError } from "../utils/api-error";

interface EditableProperty extends KnowledgeProperty {
  defaultValueText: string;
  constraintsText: string;
}

interface EditableType extends Omit<KnowledgeType, "properties"> {
  properties: EditableProperty[];
}

interface EditableTemplate extends Omit<KnowledgeTemplate, "fields"> {
  fieldsText: string;
}

interface EditableModel {
  version: number;
  updatedAt: string;
  knowledgeTypes: EditableType[];
  templates: EditableTemplate[];
  relationTypes: RelationType[];
  sourceRulesText: string;
  workflowsText: string;
}

type TabKey = "types" | "templates" | "relations" | "advanced";

const tabs: Array<{ key: TabKey; label: string }> = [
  { key: "types", label: "类型与属性" },
  { key: "templates", label: "模板" },
  { key: "relations", label: "关系类型" },
  { key: "advanced", label: "高级 JSON" },
];
const valueTypeOptions = [
  { label: "文本", value: "string" },
  { label: "数字", value: "number" },
  { label: "布尔", value: "boolean" },
  { label: "日期", value: "date" },
  { label: "枚举", value: "enum" },
  { label: "数组", value: "array" },
  { label: "对象", value: "object" },
];

const model = ref<EditableModel | null>(null);
const activeTab = ref<TabKey>("types");
const selectedTypeIndex = ref(0);
const selectedTemplateIndex = ref(0);
const selectedRelationIndex = ref(0);
const loading = ref(false);
const saving = ref(false);
const message = ref("");
const error = ref("");

const selectedType = computed(
  () => model.value?.knowledgeTypes[selectedTypeIndex.value] ?? null,
);
const selectedTemplate = computed(
  () => model.value?.templates[selectedTemplateIndex.value] ?? null,
);
const selectedRelation = computed(
  () => model.value?.relationTypes[selectedRelationIndex.value] ?? null,
);
const persistedTypes = computed(
  () =>
    model.value?.knowledgeTypes.filter(
      (type): type is EditableType & { typeId: string } =>
        typeof type.typeId === "string" && type.typeId.length > 0,
    ) ?? [],
);
const typeOptions = computed(() =>
  persistedTypes.value.map((type) => ({
    label: `${type.name} (${type.key})`,
    value: type.typeId,
  })),
);

const load = async (): Promise<void> => {
  loading.value = true;
  error.value = "";
  try {
    applyModel(await knowledgeBaseApi.knowledgeModel());
  } catch (caught) {
    error.value = describeApiError(caught);
  } finally {
    loading.value = false;
  }
};

const applyModel = (loaded: KnowledgeModel): void => {
  model.value = {
    version: loaded.version,
    updatedAt: loaded.updatedAt,
    knowledgeTypes: loaded.knowledgeTypes.map((type) => ({
      ...type,
      properties: type.properties.map((property) => ({
        ...property,
        defaultValueText: formatJson(property.defaultValue),
        constraintsText: formatJson(property.constraints ?? {}),
      })),
    })),
    templates: loaded.templates.map((template) => ({
      ...template,
      fieldsText: formatJson(template.fields ?? []),
    })),
    relationTypes: loaded.relationTypes,
    sourceRulesText: formatJson(loaded.sourceRules ?? []),
    workflowsText: formatJson(loaded.workflows ?? []),
  };
  selectedTypeIndex.value = Math.min(
    selectedTypeIndex.value,
    Math.max(0, loaded.knowledgeTypes.length - 1),
  );
  selectedTemplateIndex.value = Math.min(
    selectedTemplateIndex.value,
    Math.max(0, loaded.templates.length - 1),
  );
  selectedRelationIndex.value = Math.min(
    selectedRelationIndex.value,
    Math.max(0, loaded.relationTypes.length - 1),
  );
};

const save = async (): Promise<void> => {
  if (!model.value) return;
  saving.value = true;
  error.value = "";
  message.value = "";
  try {
    const parsed = buildModel(model.value);
    applyModel(await knowledgeBaseApi.replaceKnowledgeModel(parsed));
    message.value = `模型 v${model.value?.version ?? parsed.version} 已保存`;
  } catch (caught) {
    error.value =
      caught instanceof Error ? caught.message : describeApiError(caught);
  } finally {
    saving.value = false;
  }
};

const addType = (): void => {
  if (!model.value) return;
  model.value.knowledgeTypes.push({
    typeId: null,
    key: `type_${model.value.knowledgeTypes.length + 1}`,
    name: "新知识类型",
    description: null,
    isEnabled: true,
    sortOrder: model.value.knowledgeTypes.length,
    properties: [],
  });
  selectedTypeIndex.value = model.value.knowledgeTypes.length - 1;
  activeTab.value = "types";
};

const addProperty = (): void => {
  const type = selectedType.value;
  if (!type) return;
  type.properties.push({
    propertyId: null,
    key: `property_${type.properties.length + 1}`,
    name: "新属性",
    valueType: "string",
    required: false,
    defaultValue: null,
    constraints: {},
    isEnabled: true,
    sortOrder: type.properties.length,
    defaultValueText: "null",
    constraintsText: "{}",
  });
};

const addTemplate = (): void => {
  const type = persistedTypes.value[0];
  if (!model.value || !type) {
    error.value = "请先保存至少一个知识类型，再新增模板。";
    return;
  }
  model.value.templates.push({
    templateId: null,
    knowledgeTypeId: type.typeId,
    key: `template_${model.value.templates.length + 1}`,
    name: "新模板",
    description: null,
    bodyTemplate: "",
    isEnabled: true,
    sortOrder: model.value.templates.length,
    fieldsText: "[]",
  });
  selectedTemplateIndex.value = model.value.templates.length - 1;
  activeTab.value = "templates";
};

const addRelation = (): void => {
  if (!model.value) return;
  model.value.relationTypes.push({
    relationTypeId: null,
    key: `relation_${model.value.relationTypes.length + 1}`,
    name: "新关系类型",
    description: null,
    isDirected: true,
    isEnabled: true,
    sortOrder: model.value.relationTypes.length,
  });
  selectedRelationIndex.value = model.value.relationTypes.length - 1;
  activeTab.value = "relations";
};

const buildModel = (source: EditableModel): KnowledgeModel => ({
  version: source.version,
  updatedAt: source.updatedAt,
  knowledgeTypes: source.knowledgeTypes.map(({ properties, ...type }) => ({
    ...type,
    properties: properties.map(
      ({ defaultValueText, constraintsText, ...property }) => ({
        ...property,
        defaultValue: parseOptionalJson(
          defaultValueText,
          `${property.name} 默认值`,
        ),
        constraints: parseObject(constraintsText, `${property.name} 约束`),
      }),
    ),
  })),
  templates: source.templates.map(({ fieldsText, ...template }) => ({
    ...template,
    fields: parseArray(fieldsText, `${template.name} 字段`),
  })),
  relationTypes: source.relationTypes,
  sourceRules: parseArray<SourceRule>(source.sourceRulesText, "来源规则"),
  workflows: parseArray<Workflow>(source.workflowsText, "工作流"),
});

const formatJson = (value: unknown): string =>
  JSON.stringify(value ?? null, null, 2);

const parseOptionalJson = (text: string, label: string): unknown => {
  if (!text.trim()) return null;
  return parseJson(text, label);
};

const parseObject = (text: string, label: string): Record<string, unknown> => {
  if (!text.trim()) return {};
  const value = parseJson(text, label);
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`${label}必须是 JSON 对象。`);
  }
  return value as Record<string, unknown>;
};

const parseArray = <T,>(text: string, label: string): T[] => {
  if (!text.trim()) return [];
  const value = parseJson(text, label);
  if (!Array.isArray(value)) {
    throw new Error(`${label}必须是 JSON 数组。`);
  }
  return value as T[];
};

const parseJson = (text: string, label: string): unknown => {
  try {
    return JSON.parse(text);
  } catch {
    throw new Error(`${label}不是有效 JSON。`);
  }
};

onMounted(load);
</script>

<template>
  <section class="page">
    <header class="page-header">
      <div>
        <p class="eyebrow">Knowledge Model</p>
        <h1>知识模型</h1>
        <p v-if="model" class="muted">
          版本 {{ model.version }} · 更新于
          {{ new Date(model.updatedAt).toLocaleString("zh-CN") }}
        </p>
      </div>
      <NButton
        type="primary"
        :loading="saving"
        :disabled="!model"
        @click="save"
      >
        <template #icon>
          <NIcon :component="Save" aria-hidden="true" />
        </template>
        保存模型
      </NButton>
    </header>

    <div v-if="loading" class="status">
      <NSpin size="small" />
      正在读取模型
    </div>
    <NAlert v-if="error" type="error" :show-icon="false">{{ error }}</NAlert>
    <NAlert v-if="message" type="success" :show-icon="false">
      {{ message }}
    </NAlert>

    <template v-if="model">
      <div class="model-summary">
        <span>{{ model.knowledgeTypes.length }} 个类型</span>
        <span>{{ model.templates.length }} 个模板</span>
        <span>{{ model.relationTypes.length }} 个关系</span>
      </div>

      <div class="tabs" role="tablist" aria-label="知识模型区域">
        <button
          v-for="tab in tabs"
          :key="tab.key"
          type="button"
          role="tab"
          :aria-selected="activeTab === tab.key"
          :aria-controls="`model-panel-${tab.key}`"
          :class="{ active: activeTab === tab.key }"
          @click="activeTab = tab.key"
        >
          {{ tab.label }}
        </button>
      </div>

      <section
        v-if="activeTab === 'types'"
        id="model-panel-types"
        class="editor-layout"
        role="tabpanel"
      >
        <aside aria-label="知识类型">
          <NButton block secondary @click="addType">
            <template #icon>
              <NIcon :component="Plus" aria-hidden="true" />
            </template>
            新增类型
          </NButton>
          <button
            v-for="(type, index) in model.knowledgeTypes"
            :key="type.typeId ?? index"
            type="button"
            class="entity-button"
            :class="{ active: selectedTypeIndex === index }"
            @click="selectedTypeIndex = index"
          >
            <strong>{{ type.name || "未命名类型" }}</strong>
            <span>{{ type.key || "未设置 key" }}</span>
            <small>{{ type.isEnabled ? "启用" : "停用" }}</small>
          </button>
        </aside>

        <div v-if="selectedType" class="editor-panel">
          <div class="field-grid">
            <label>
              <span>稳定 key</span>
              <NInput
                v-model:value="selectedType.key"
                :disabled="Boolean(selectedType.typeId)"
                :input-props="{ 'aria-label': '类型稳定 key' }"
              />
            </label>
            <label>
              <span>名称</span>
              <NInput
                v-model:value="selectedType.name"
                :input-props="{ 'aria-label': '类型名称' }"
              />
            </label>
            <label class="wide">
              <span>说明</span>
              <NInput
                v-model:value="selectedType.description"
                type="textarea"
                :autosize="{ minRows: 2, maxRows: 5 }"
                :input-props="{ 'aria-label': '类型说明' }"
              />
            </label>
            <label>
              <span>排序</span>
              <NInputNumber
                v-model:value="selectedType.sortOrder"
                :min="0"
                :input-props="{ 'aria-label': '类型排序' }"
              />
            </label>
            <NCheckbox v-model:checked="selectedType.isEnabled">
              启用类型
            </NCheckbox>
          </div>

          <div class="section-heading">
            <h2>属性</h2>
            <NButton size="small" secondary @click="addProperty">
              <template #icon>
                <NIcon :component="Plus" aria-hidden="true" />
              </template>
              新增属性
            </NButton>
          </div>

          <div class="property-list">
            <article
              v-for="(property, index) in selectedType.properties"
              :key="property.propertyId ?? index"
              class="property-item"
            >
              <div class="field-grid">
                <label>
                  <span>属性 key</span>
                  <NInput
                    v-model:value="property.key"
                    :disabled="Boolean(property.propertyId)"
                    :input-props="{ 'aria-label': '属性 key' }"
                  />
                </label>
                <label>
                  <span>属性名称</span>
                  <NInput
                    v-model:value="property.name"
                    :input-props="{ 'aria-label': '属性名称' }"
                  />
                </label>
                <label>
                  <span>值类型</span>
                  <NSelect
                    v-model:value="property.valueType"
                    :options="valueTypeOptions"
                    :input-props="{ 'aria-label': '属性值类型' }"
                  />
                </label>
                <label>
                  <span>排序</span>
                  <NInputNumber
                    v-model:value="property.sortOrder"
                    :min="0"
                    :input-props="{ 'aria-label': '属性排序' }"
                  />
                </label>
                <NCheckbox v-model:checked="property.required">
                  必填
                </NCheckbox>
                <NCheckbox v-model:checked="property.isEnabled">
                  启用属性
                </NCheckbox>
                <label class="wide">
                  <span>默认值 JSON</span>
                  <NInput
                    v-model:value="property.defaultValueText"
                    type="textarea"
                    :autosize="{ minRows: 2, maxRows: 6 }"
                    :input-props="{ 'aria-label': '属性默认值 JSON' }"
                  />
                </label>
                <label class="wide">
                  <span>约束 JSON</span>
                  <NInput
                    v-model:value="property.constraintsText"
                    type="textarea"
                    :autosize="{ minRows: 2, maxRows: 6 }"
                    :input-props="{ 'aria-label': '属性约束 JSON' }"
                  />
                </label>
              </div>
            </article>
            <p v-if="selectedType.properties.length === 0" class="empty">
              暂无属性。
            </p>
          </div>
        </div>
        <div v-else class="editor-panel empty">
          新增一个知识类型后开始编辑。
        </div>
      </section>

      <section
        v-if="activeTab === 'templates'"
        id="model-panel-templates"
        class="editor-layout"
        role="tabpanel"
      >
        <aside aria-label="知识模板">
          <NButton
            block
            secondary
            :disabled="persistedTypes.length === 0"
            @click="addTemplate"
          >
            <template #icon>
              <NIcon :component="Plus" aria-hidden="true" />
            </template>
            新增模板
          </NButton>
          <button
            v-for="(template, index) in model.templates"
            :key="template.templateId ?? index"
            type="button"
            class="entity-button"
            :class="{ active: selectedTemplateIndex === index }"
            @click="selectedTemplateIndex = index"
          >
            <strong>{{ template.name || "未命名模板" }}</strong>
            <span>{{ template.key || "未设置 key" }}</span>
            <small>{{ template.isEnabled ? "启用" : "停用" }}</small>
          </button>
        </aside>

        <div v-if="selectedTemplate" class="editor-panel">
          <div class="field-grid">
            <label>
              <span>所属类型</span>
              <NSelect
                v-model:value="selectedTemplate.knowledgeTypeId"
                :options="typeOptions"
                :input-props="{ 'aria-label': '模板所属类型' }"
              />
            </label>
            <label>
              <span>稳定 key</span>
              <NInput
                v-model:value="selectedTemplate.key"
                :disabled="Boolean(selectedTemplate.templateId)"
                :input-props="{ 'aria-label': '模板稳定 key' }"
              />
            </label>
            <label>
              <span>名称</span>
              <NInput
                v-model:value="selectedTemplate.name"
                :input-props="{ 'aria-label': '模板名称' }"
              />
            </label>
            <label>
              <span>排序</span>
              <NInputNumber
                v-model:value="selectedTemplate.sortOrder"
                :min="0"
                :input-props="{ 'aria-label': '模板排序' }"
              />
            </label>
            <label class="wide">
              <span>说明</span>
              <NInput
                v-model:value="selectedTemplate.description"
                type="textarea"
                :autosize="{ minRows: 2, maxRows: 5 }"
                :input-props="{ 'aria-label': '模板说明' }"
              />
            </label>
            <label class="wide">
              <span>正文模板</span>
              <NInput
                v-model:value="selectedTemplate.bodyTemplate"
                type="textarea"
                :autosize="{ minRows: 8, maxRows: 20 }"
                :input-props="{ 'aria-label': '模板正文' }"
              />
            </label>
            <label class="wide">
              <span>字段 JSON</span>
              <NInput
                v-model:value="selectedTemplate.fieldsText"
                type="textarea"
                :autosize="{ minRows: 6, maxRows: 18 }"
                :input-props="{ 'aria-label': '模板字段 JSON' }"
              />
            </label>
            <NCheckbox v-model:checked="selectedTemplate.isEnabled">
              启用模板
            </NCheckbox>
          </div>
        </div>
        <div v-else class="editor-panel empty">暂无模板。</div>
      </section>

      <section
        v-if="activeTab === 'relations'"
        id="model-panel-relations"
        class="editor-layout"
        role="tabpanel"
      >
        <aside aria-label="关系类型">
          <NButton block secondary @click="addRelation">
            <template #icon>
              <NIcon :component="Plus" aria-hidden="true" />
            </template>
            新增关系
          </NButton>
          <button
            v-for="(relation, index) in model.relationTypes"
            :key="relation.relationTypeId ?? index"
            type="button"
            class="entity-button"
            :class="{ active: selectedRelationIndex === index }"
            @click="selectedRelationIndex = index"
          >
            <strong>{{ relation.name || "未命名关系" }}</strong>
            <span>{{ relation.key || "未设置 key" }}</span>
            <small>{{ relation.isEnabled ? "启用" : "停用" }}</small>
          </button>
        </aside>

        <div v-if="selectedRelation" class="editor-panel">
          <div class="field-grid">
            <label>
              <span>稳定 key</span>
              <NInput
                v-model:value="selectedRelation.key"
                :disabled="Boolean(selectedRelation.relationTypeId)"
                :input-props="{ 'aria-label': '关系稳定 key' }"
              />
            </label>
            <label>
              <span>名称</span>
              <NInput
                v-model:value="selectedRelation.name"
                :input-props="{ 'aria-label': '关系名称' }"
              />
            </label>
            <label>
              <span>排序</span>
              <NInputNumber
                v-model:value="selectedRelation.sortOrder"
                :min="0"
                :input-props="{ 'aria-label': '关系排序' }"
              />
            </label>
            <label class="wide">
              <span>说明</span>
              <NInput
                v-model:value="selectedRelation.description"
                type="textarea"
                :autosize="{ minRows: 2, maxRows: 5 }"
                :input-props="{ 'aria-label': '关系说明' }"
              />
            </label>
            <NCheckbox v-model:checked="selectedRelation.isDirected">
              有向关系
            </NCheckbox>
            <NCheckbox v-model:checked="selectedRelation.isEnabled">
              启用关系
            </NCheckbox>
          </div>
        </div>
        <div v-else class="editor-panel empty">暂无关系类型。</div>
      </section>

      <section
        v-if="activeTab === 'advanced'"
        id="model-panel-advanced"
        class="advanced"
        role="tabpanel"
      >
        <label>
          <span>来源规则 JSON</span>
          <NInput
            v-model:value="model.sourceRulesText"
            type="textarea"
            :autosize="{ minRows: 12, maxRows: 28 }"
            :input-props="{ 'aria-label': '来源规则 JSON' }"
          />
        </label>
        <label>
          <span>工作流约束 JSON</span>
          <NInput
            v-model:value="model.workflowsText"
            type="textarea"
            :autosize="{ minRows: 12, maxRows: 28 }"
            :input-props="{ 'aria-label': '工作流约束 JSON' }"
          />
        </label>
      </section>
    </template>
  </section>
</template>

<style scoped>
.page {
  max-width: 1120px;
  margin: 0 auto;
  padding: 28px;
}
.page-header {
  display: flex;
  align-items: start;
  justify-content: space-between;
  gap: 16px;
}
.eyebrow {
  margin: 0 0 6px;
  color: var(--cv-accent);
  font-size: 11px;
  text-transform: uppercase;
}
h1 {
  margin: 0;
  font-size: 28px;
}
h2 {
  margin: 0;
  font-size: 15px;
}
.muted,
.empty {
  color: var(--cv-muted);
}
.status {
  display: flex;
  min-height: 120px;
  align-items: center;
  justify-content: center;
  gap: 10px;
  color: var(--cv-muted);
}
.model-summary {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin: 20px 0 14px;
}
.model-summary span {
  padding: 5px 9px;
  border: 1px solid var(--cv-border);
  border-radius: 6px;
  color: var(--cv-muted);
  font-size: 12px;
}
.tabs {
  display: flex;
  gap: 4px;
  padding-bottom: 8px;
  border-bottom: 1px solid var(--cv-border);
}
.tabs button {
  padding: 8px 11px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--cv-muted);
  cursor: pointer;
}
.tabs button.active {
  background: color-mix(in srgb, var(--cv-accent) 12%, transparent);
  color: var(--cv-accent);
}
.editor-layout {
  display: grid;
  grid-template-columns: 240px minmax(0, 1fr);
  gap: 16px;
  margin-top: 16px;
}
.editor-layout aside {
  display: grid;
  align-content: start;
  gap: 6px;
}
.entity-button {
  display: grid;
  gap: 3px;
  padding: 10px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-surface);
  color: inherit;
  cursor: pointer;
  text-align: left;
}
.entity-button.active {
  border-color: var(--cv-accent);
}
.entity-button span,
.entity-button small,
.field-grid span {
  color: var(--cv-muted);
  font-size: 11px;
}
.editor-panel,
.advanced {
  min-width: 0;
  padding: 16px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}
.editor-panel.empty {
  min-height: 180px;
  display: grid;
  place-items: center;
}
.field-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
}
.field-grid label,
.advanced label {
  display: grid;
  gap: 5px;
}
.field-grid .wide,
.advanced label {
  grid-column: 1 / -1;
}
.section-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin: 22px 0 10px;
}
.property-list {
  display: grid;
  gap: 10px;
}
.property-item {
  padding: 14px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
}
.advanced {
  display: grid;
  gap: 16px;
  margin-top: 16px;
}
@media (max-width: 850px) {
  .page {
    padding: 18px;
  }
  .page-header,
  .section-heading {
    align-items: stretch;
    flex-direction: column;
  }
  .editor-layout,
  .field-grid {
    grid-template-columns: 1fr;
  }
  .field-grid .wide,
  .advanced label {
    grid-column: auto;
  }
  .tabs {
    overflow-x: auto;
  }
  .tabs button {
    white-space: nowrap;
  }
}
</style>
