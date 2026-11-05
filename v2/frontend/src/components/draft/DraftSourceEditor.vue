<script setup lang="ts">
import { computed, ref } from "vue";
import type {
  DraftValidationIssue,
  KnowledgeModel,
} from "../../types/knowledge-base";
import type { SourceDraftPayload } from "../../types/draft-editor";
import { draftFieldId } from "../../utils/draft-editor";

const props = defineProps<{
  payload: SourceDraftPayload;
  model: KnowledgeModel;
  readonly: boolean;
  issues: DraftValidationIssue[];
}>();

const emit = defineEmits<{
  (event: "update:payload", value: SourceDraftPayload): void;
}>();

const jsonErrors = ref<Record<string, string>>({});
const enabledRules = computed(() =>
  props.model.sourceRules.filter((rule) => rule.isEnabled),
);
const currentRule = computed(() =>
  enabledRules.value.find((rule) => rule.kind === props.payload.sourceKind),
);
const metadataProperties = computed(() => {
  const properties = currentRule.value?.metadataSchema.properties;
  return properties && typeof properties === "object"
    ? (properties as Record<string, Record<string, unknown>>)
    : {};
});
const requiredProperties = computed(() => {
  const required = currentRule.value?.metadataSchema.required;
  return Array.isArray(required)
    ? new Set(
        required.filter((item): item is string => typeof item === "string"),
      )
    : new Set<string>();
});

const update = (patch: Partial<SourceDraftPayload>) => {
  emit("update:payload", { ...props.payload, ...patch });
};

const changeKind = (event: Event) => {
  update({
    sourceKind: (event.target as HTMLSelectElement).value,
    metadata: {},
  });
};

const updateMetadata = (key: string, value: unknown) => {
  const metadata = { ...props.payload.metadata };
  if (value === undefined) {
    delete metadata[key];
  } else {
    metadata[key] = value;
  }
  update({ metadata });
};

const updateMetadataNumber = (key: string, event: Event) => {
  const value = (event.target as HTMLInputElement).value;
  updateMetadata(key, value === "" ? undefined : Number(value));
};

const updateMetadataJson = (key: string, event: Event) => {
  const value = (event.target as HTMLTextAreaElement).value;
  const path = `metadata.${key}`;
  try {
    updateMetadata(key, JSON.parse(value));
    delete jsonErrors.value[path];
  } catch {
    jsonErrors.value[path] = "JSON 格式无效。";
  }
};

const metadataJson = (key: string): string =>
  JSON.stringify(props.payload.metadata[key] ?? null, null, 2);

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

const propertyType = (key: string): string =>
  typeof metadataProperties.value[key]?.type === "string"
    ? String(metadataProperties.value[key]?.type)
    : "string";
</script>

<template>
  <div class="editor">
    <label class="field">
      <span>来源类型</span>
      <select
        :value="payload.sourceKind"
        :disabled="readonly"
        @change="changeKind"
      >
        <option
          v-for="rule in enabledRules"
          :key="rule.kind"
          :value="rule.kind"
        >
          {{ rule.kind }}
        </option>
      </select>
    </label>

    <label class="field">
      <span>标题</span>
      <input
        :id="draftFieldId('title')"
        :value="payload.title"
        :disabled="readonly"
        @input="update({ title: ($event.target as HTMLInputElement).value })"
      />
      <small v-if="issueDescription('title')" class="field-error">
        {{ issueDescription("title") }}
      </small>
    </label>

    <section class="section">
      <h3>Metadata</h3>
      <p v-if="Object.keys(metadataProperties).length === 0" class="muted">
        当前来源类型没有可配置 metadata。
      </p>
      <label
        v-for="key in Object.keys(metadataProperties)"
        :key="key"
        class="field"
      >
        <span>
          {{ key }}
          <em v-if="requiredProperties.has(key)">必填</em>
        </span>
        <template v-if="propertyType(key) === 'number'">
          <input
            type="number"
            :value="payload.metadata[key] ?? ''"
            :disabled="readonly"
            @input="updateMetadataNumber(key, $event)"
          />
        </template>
        <template
          v-else-if="
            propertyType(key) === 'array' || propertyType(key) === 'object'
          "
        >
          <textarea
            rows="4"
            :value="metadataJson(key)"
            :disabled="readonly"
            @change="updateMetadataJson(key, $event)"
          />
        </template>
        <template v-else>
          <input
            :type="propertyType(key) === 'date' ? 'date' : 'text'"
            :value="payload.metadata[key] ?? ''"
            :disabled="readonly"
            @input="
              updateMetadata(
                key,
                ($event.target as HTMLInputElement).value || undefined,
              )
            "
          />
        </template>
        <small v-if="issueDescription(`metadata.${key}`)" class="field-error">
          {{ issueDescription(`metadata.${key}`) }}
        </small>
      </label>
    </section>

    <label class="field">
      <span>内容格式</span>
      <select
        :value="payload.contentFormat"
        :disabled="readonly"
        @change="
          update({
            contentFormat: ($event.target as HTMLSelectElement).value,
          })
        "
      >
        <option value="markdown">Markdown</option>
        <option value="text">Text</option>
      </select>
    </label>

    <label class="field">
      <span>内容快照</span>
      <textarea
        :id="draftFieldId('contentText')"
        :value="payload.contentText"
        :disabled="readonly"
        rows="16"
        @input="
          update({
            contentText: ($event.target as HTMLTextAreaElement).value,
          })
        "
      />
    </label>
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

textarea {
  resize: vertical;
}

.field-error {
  color: #b93f36;
}

.muted {
  color: var(--cv-muted);
}
</style>
