<script setup lang="ts">
import { computed, ref } from "vue";
import type {
  DraftValidationIssue,
  KnowledgeModel,
  KnowledgeProperty,
} from "../../types/knowledge-base";
import type { KnowledgeDraftPayload } from "../../types/draft-editor";
import { draftFieldId } from "../../utils/draft-editor";

const props = defineProps<{
  payload: KnowledgeDraftPayload;
  model: KnowledgeModel;
  readonly: boolean;
  issues: DraftValidationIssue[];
}>();

const emit = defineEmits<{
  (event: "update:payload", value: KnowledgeDraftPayload): void;
}>();

const jsonErrors = ref<Record<string, string>>({});
const enabledTypes = computed(() =>
  props.model.knowledgeTypes.filter((type) => type.isEnabled && type.typeId),
);
const currentType = computed(() =>
  enabledTypes.value.find(
    (type) => type.typeId === props.payload.knowledgeTypeId,
  ),
);
const templates = computed(() =>
  props.model.templates.filter(
    (template) =>
      template.isEnabled &&
      template.knowledgeTypeId === props.payload.knowledgeTypeId,
  ),
);

const update = (patch: Partial<KnowledgeDraftPayload>) => {
  emit("update:payload", { ...props.payload, ...patch });
};

const changeType = (event: Event) => {
  update({
    knowledgeTypeId: (event.target as HTMLSelectElement).value,
    templateId: null,
    properties: {},
  });
};

const updateTags = (event: Event) => {
  const values = (event.target as HTMLInputElement).value
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
  update({ tags: [...new Set(values)] });
};

const updateProperty = (key: string, value: unknown) => {
  const properties = { ...props.payload.properties };
  if (value === undefined) {
    delete properties[key];
  } else {
    properties[key] = value;
  }
  update({ properties });
};

const updatePropertyNumber = (property: KnowledgeProperty, event: Event) => {
  const value = (event.target as HTMLInputElement).value;
  updateProperty(property.key, value === "" ? undefined : Number(value));
};

const updatePropertyJson = (property: KnowledgeProperty, event: Event) => {
  const value = (event.target as HTMLTextAreaElement).value;
  const fieldPath = `properties.${property.key}`;
  try {
    updateProperty(property.key, JSON.parse(value));
    delete jsonErrors.value[fieldPath];
  } catch {
    jsonErrors.value[fieldPath] = "JSON 格式无效。";
  }
};

const propertyJson = (property: KnowledgeProperty): string =>
  JSON.stringify(props.payload.properties[property.key] ?? null, null, 2);

const issuesFor = (fieldPath: string) =>
  props.issues.filter(
    (issue) =>
      issue.fieldPath === fieldPath ||
      issue.fieldPath.startsWith(`${fieldPath}.`),
  );

const issueDescription = (fieldPath: string): string | undefined => {
  const issues = issuesFor(fieldPath);
  return issues.length
    ? issues.map((issue) => issue.message).join(" ")
    : jsonErrors.value[fieldPath];
};

const propertyOptions = (property: KnowledgeProperty): string[] => {
  const options = property.constraints.options;
  return Array.isArray(options)
    ? options.filter((item): item is string => typeof item === "string")
    : [];
};

const sourceReferenceLabel = (index: number): string => `来源引用 ${index + 1}`;
</script>

<template>
  <div class="editor">
    <label class="field">
      <span>标题</span>
      <input
        :id="draftFieldId('title')"
        :value="payload.title"
        :disabled="readonly"
        :aria-describedby="
          issueDescription('title')
            ? draftFieldId('title') + '-error'
            : undefined
        "
        @input="update({ title: ($event.target as HTMLInputElement).value })"
      />
      <small
        v-if="issueDescription('title')"
        :id="draftFieldId('title') + '-error'"
        class="field-error"
      >
        {{ issueDescription("title") }}
      </small>
    </label>

    <label class="field">
      <span>摘要</span>
      <textarea
        :id="draftFieldId('summary')"
        :value="payload.summary"
        :disabled="readonly"
        rows="3"
        :aria-describedby="
          issueDescription('summary')
            ? draftFieldId('summary') + '-error'
            : undefined
        "
        @input="
          update({ summary: ($event.target as HTMLTextAreaElement).value })
        "
      />
      <small
        v-if="issueDescription('summary')"
        :id="draftFieldId('summary') + '-error'"
        class="field-error"
      >
        {{ issueDescription("summary") }}
      </small>
    </label>

    <label class="field">
      <span>知识类型</span>
      <select
        :id="draftFieldId('knowledgeTypeId')"
        :value="payload.knowledgeTypeId"
        :disabled="readonly"
        @change="changeType"
      >
        <option
          v-for="type in enabledTypes"
          :key="type.typeId ?? type.key"
          :value="type.typeId ?? ''"
        >
          {{ type.name }}
        </option>
      </select>
    </label>

    <label class="field">
      <span>模板</span>
      <select
        :value="payload.templateId ?? ''"
        :disabled="readonly"
        @change="
          update({
            templateId: ($event.target as HTMLSelectElement).value || null,
          })
        "
      >
        <option value="">不指定模板</option>
        <option
          v-for="template in templates"
          :key="template.templateId ?? template.key"
          :value="template.templateId ?? ''"
        >
          {{ template.name }}
        </option>
      </select>
    </label>

    <label class="field">
      <span>正文 Markdown</span>
      <textarea
        :id="draftFieldId('contentMarkdown')"
        :value="payload.contentMarkdown"
        :disabled="readonly"
        rows="14"
        @input="
          update({
            contentMarkdown: ($event.target as HTMLTextAreaElement).value,
          })
        "
      />
    </label>

    <section class="section">
      <h3>属性</h3>
      <p v-if="!currentType?.properties.length" class="muted">
        当前类型没有属性。
      </p>
      <label
        v-for="property in currentType?.properties.filter(
          (item) => item.isEnabled,
        )"
        :key="property.propertyId ?? property.key"
        class="field"
      >
        <span>
          {{ property.name }}
          <em v-if="property.required">必填</em>
        </span>
        <template v-if="property.valueType === 'boolean'">
          <input
            type="checkbox"
            :checked="payload.properties[property.key] === true"
            :disabled="readonly"
            @change="
              updateProperty(
                property.key,
                ($event.target as HTMLInputElement).checked,
              )
            "
          />
        </template>
        <template v-else-if="property.valueType === 'number'">
          <input
            type="number"
            :value="payload.properties[property.key] ?? ''"
            :disabled="readonly"
            @input="updatePropertyNumber(property, $event)"
          />
        </template>
        <template
          v-else-if="
            property.valueType === 'array' || property.valueType === 'object'
          "
        >
          <textarea
            rows="5"
            :value="propertyJson(property)"
            :disabled="readonly"
            @change="updatePropertyJson(property, $event)"
          />
        </template>
        <template
          v-else-if="
            property.valueType === 'enum' && propertyOptions(property).length
          "
        >
          <select
            :value="String(payload.properties[property.key] ?? '')"
            :disabled="readonly"
            @change="
              updateProperty(
                property.key,
                ($event.target as HTMLSelectElement).value,
              )
            "
          >
            <option value="">未设置</option>
            <option
              v-for="option in propertyOptions(property)"
              :key="option"
              :value="option"
            >
              {{ option }}
            </option>
          </select>
        </template>
        <template v-else>
          <input
            :type="property.valueType === 'date' ? 'date' : 'text'"
            :value="payload.properties[property.key] ?? ''"
            :disabled="readonly"
            @input="
              updateProperty(
                property.key,
                ($event.target as HTMLInputElement).value || undefined,
              )
            "
          />
        </template>
        <small
          v-if="issueDescription(`properties.${property.key}`)"
          class="field-error"
        >
          {{ issueDescription(`properties.${property.key}`) }}
        </small>
      </label>
    </section>

    <label class="field">
      <span>标签</span>
      <input
        :value="payload.tags.join(', ')"
        :disabled="readonly"
        placeholder="用逗号分隔"
        @input="updateTags"
      />
    </label>

    <section class="section">
      <h3>来源依据</h3>
      <p v-if="payload.sourceReferences.length === 0" class="muted">
        没有来源引用。
      </p>
      <article
        v-for="(reference, index) in payload.sourceReferences"
        :key="reference.sourceReferenceId || `${reference.sourceId}-${index}`"
        class="source-reference"
      >
        <strong>{{ sourceReferenceLabel(index) }}</strong>
        <span>
          {{ reference.sourceId }} · revision {{ reference.sourceRevision }}
        </span>
        <code
          >{{ reference.locatorType }}
          {{ JSON.stringify(reference.locatorValue) }}</code
        >
        <blockquote>{{ reference.excerpt }}</blockquote>
      </article>
    </section>
  </div>
</template>

<style scoped>
.editor,
.section,
.field {
  display: grid;
  gap: 8px;
}

.editor {
  gap: 14px;
}

.section {
  padding-top: 8px;
  border-top: 1px solid var(--cv-border);
}

h3 {
  margin: 0;
  font-size: 15px;
}

.field {
  font-size: 13px;
}

.field > span {
  color: var(--cv-muted);
}

.field em {
  color: #b93f36;
  font-size: 11px;
  font-style: normal;
}

input,
select,
textarea {
  width: 100%;
  min-height: 38px;
  padding: 9px 10px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-surface);
  color: var(--cv-text);
  font: inherit;
}

input[type="checkbox"] {
  width: 18px;
  min-height: 18px;
}

textarea {
  resize: vertical;
}

.field-error {
  color: #b93f36;
}

.muted {
  color: var(--cv-muted);
}

.source-reference {
  display: grid;
  gap: 4px;
  padding: 10px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-bg);
}

.source-reference span,
.source-reference code {
  color: var(--cv-muted);
  font-size: 12px;
  overflow-wrap: anywhere;
}

blockquote {
  margin: 4px 0 0;
  padding-left: 10px;
  border-left: 2px solid var(--cv-accent);
}
</style>
