<script setup lang="ts">
import { ref } from "vue";
import { knowledgeBaseApi } from "../../api/knowledge-base";
import type {
  DraftValidationIssue,
  KnowledgeSummary,
} from "../../types/knowledge-base";
import { draftFieldId } from "../../utils/draft-editor";

const props = defineProps<{
  label: string;
  fieldPath: string;
  selectedId: string;
  selectedRevision: number;
  readonly: boolean;
  issues: DraftValidationIssue[];
}>();

const emit = defineEmits<{
  (event: "select", value: KnowledgeSummary): void;
  (event: "update:revision", value: number): void;
}>();

const query = ref("");
const results = ref<KnowledgeSummary[]>([]);
const searching = ref(false);
const error = ref<string | null>(null);

const search = async () => {
  const text = query.value.trim();
  if (!text) {
    results.value = [];
    return;
  }
  searching.value = true;
  error.value = null;
  try {
    const page = await knowledgeBaseApi.searchKnowledge({
      query: text,
      mode: "keyword",
      pageSize: 20,
    });
    results.value = page.items;
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "搜索失败";
  } finally {
    searching.value = false;
  }
};

const select = (item: KnowledgeSummary) => {
  emit("select", item);
  results.value = [];
  query.value = "";
};

const issueText = () =>
  props.issues
    .filter((issue) => issue.fieldPath === props.fieldPath)
    .map((issue) => issue.message)
    .join(" ");
</script>

<template>
  <section class="picker">
    <label class="field">
      <span>{{ label }}</span>
      <input
        :id="draftFieldId(fieldPath)"
        v-model="query"
        :disabled="readonly"
        placeholder="搜索 Knowledge 标题或正文"
        @keyup.enter.prevent="search"
      />
    </label>
    <div class="search-row">
      <button
        type="button"
        :disabled="readonly || searching || !query.trim()"
        @click="search"
      >
        {{ searching ? "搜索中" : "搜索" }}
      </button>
      <code>{{ selectedId || "未选择" }}</code>
    </div>
    <p v-if="error" class="field-error">{{ error }}</p>
    <div v-if="results.length" class="results" role="listbox">
      <button
        v-for="item in results"
        :key="item.knowledgeId"
        type="button"
        role="option"
        @click="select(item)"
      >
        <strong>{{ item.title }}</strong>
        <span>{{ item.knowledgeTypeKey }} · v{{ item.revision }}</span>
      </button>
    </div>
    <label class="field revision">
      <span>Knowledge revision</span>
      <input
        type="number"
        min="1"
        :value="selectedRevision"
        :disabled="readonly"
        @input="
          emit(
            'update:revision',
            Number(($event.target as HTMLInputElement).value),
          )
        "
      />
    </label>
    <small v-if="issueText()" class="field-error">{{ issueText() }}</small>
  </section>
</template>

<style scoped>
.picker,
.field {
  display: grid;
  gap: 7px;
}

.picker {
  padding: 10px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-bg);
}

.field {
  font-size: 13px;
}

.field > span {
  color: var(--cv-muted);
}

input,
button {
  min-height: 36px;
  padding: 7px 10px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-surface);
  color: var(--cv-text);
}

button {
  cursor: pointer;
}

button:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}

.search-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

code {
  overflow-wrap: anywhere;
  color: var(--cv-muted);
  font-size: 12px;
}

.results {
  display: grid;
  gap: 5px;
}

.results button {
  display: grid;
  gap: 3px;
  text-align: left;
}

.results span {
  color: var(--cv-muted);
  font-size: 12px;
}

.revision input {
  max-width: 160px;
}

.field-error {
  color: #b93f36;
}
</style>
