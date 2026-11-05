<script setup lang="ts">
import { computed, ref } from "vue";
import type {
  DraftValidationIssue,
  KnowledgeModel,
  KnowledgeSummary,
} from "../../types/knowledge-base";
import type { RelationDraftPayload } from "../../types/draft-editor";
import DraftKnowledgePicker from "./DraftKnowledgePicker.vue";

const props = defineProps<{
  payload: RelationDraftPayload;
  model: KnowledgeModel;
  readonly: boolean;
  issues: DraftValidationIssue[];
}>();

const emit = defineEmits<{
  (event: "update:payload", value: RelationDraftPayload): void;
}>();

const localError = ref<string | null>(null);
const relationTypes = computed(() =>
  props.model.relationTypes.filter((type) => type.isEnabled),
);

const update = (patch: Partial<RelationDraftPayload>) => {
  emit("update:payload", { ...props.payload, ...patch });
  localError.value = null;
};

const selectSource = (item: KnowledgeSummary) => {
  if (item.knowledgeId === props.payload.targetKnowledgeId) {
    localError.value = "关系两端不能是同一 Knowledge。";
    return;
  }
  update({
    sourceKnowledgeId: item.knowledgeId,
    sourceKnowledgeRevision: item.revision,
  });
};

const selectTarget = (item: KnowledgeSummary) => {
  if (item.knowledgeId === props.payload.sourceKnowledgeId) {
    localError.value = "关系两端不能是同一 Knowledge。";
    return;
  }
  update({
    targetKnowledgeId: item.knowledgeId,
    targetKnowledgeRevision: item.revision,
  });
};
</script>

<template>
  <div class="editor">
    <label class="field">
      <span>关系类型</span>
      <select
        :value="payload.relationTypeId"
        :disabled="readonly"
        @change="
          update({
            relationTypeId: ($event.target as HTMLSelectElement).value,
          })
        "
      >
        <option
          v-for="type in relationTypes"
          :key="type.relationTypeId ?? type.key"
          :value="type.relationTypeId ?? ''"
        >
          {{ type.name }}
          <template v-if="!type.isDirected">（无方向）</template>
        </option>
      </select>
    </label>

    <DraftKnowledgePicker
      label="源 Knowledge"
      field-path="sourceKnowledgeId"
      :selected-id="payload.sourceKnowledgeId"
      :selected-revision="payload.sourceKnowledgeRevision"
      :readonly="readonly"
      :issues="issues"
      @select="selectSource"
      @update:revision="update({ sourceKnowledgeRevision: $event })"
    />

    <DraftKnowledgePicker
      label="目标 Knowledge"
      field-path="targetKnowledgeId"
      :selected-id="payload.targetKnowledgeId"
      :selected-revision="payload.targetKnowledgeRevision"
      :readonly="readonly"
      :issues="issues"
      @select="selectTarget"
      @update:revision="update({ targetKnowledgeRevision: $event })"
    />

    <p v-if="localError" class="field-error">{{ localError }}</p>

    <label class="field">
      <span>关系理由</span>
      <textarea
        :value="payload.rationale"
        :disabled="readonly"
        rows="6"
        @input="
          update({
            rationale: ($event.target as HTMLTextAreaElement).value,
          })
        "
      />
    </label>

    <section class="section">
      <h3>证据（只读）</h3>
      <pre tabindex="0">{{ JSON.stringify(payload.evidence, null, 2) }}</pre>
    </section>
  </div>
</template>

<style scoped>
.editor,
.field {
  display: grid;
  gap: 8px;
}

.editor {
  gap: 14px;
}

.field {
  font-size: 13px;
}

.field > span {
  color: var(--cv-muted);
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

.section {
  padding-top: 8px;
  border-top: 1px solid var(--cv-border);
}

h3 {
  margin: 0;
  font-size: 15px;
}

pre {
  max-height: 240px;
  overflow: auto;
  padding: 10px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-bg);
  white-space: pre-wrap;
}

.field-error {
  color: #b93f36;
}
</style>
