<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { RouterLink } from "vue-router";
import { knowledgeBaseApi } from "../api/knowledge-base";
import type {
  DraftMutation,
  KnowledgeModel,
  KnowledgeSummary,
  KnowledgeSynthesisHandoff,
  PagedResult,
} from "../types/knowledge-base";

const maxSelection = 20;
const maxTopicLength = 193;
const maxQuestionLength = 4_000;
const defaultSynthesisQuestion =
  "围绕所选知识比较共同观点、分歧与冲突、证据质量和适用条件，并形成可审核的综合结论。";

const query = ref("");
const mode = ref<"keyword" | "fulltext">("keyword");
const page = ref<PagedResult<KnowledgeSummary> | null>(null);
const loading = ref(false);
const error = ref<string | null>(null);
const model = ref<KnowledgeModel | null>(null);
const modelError = ref<string | null>(null);
const selectedKnowledge = ref<KnowledgeSummary[]>([]);

const retrospectiveOpen = ref(false);
const topic = ref("");
const creating = ref(false);
const createError = ref<string | null>(null);
const createdDraft = ref<DraftMutation | null>(null);
const submissionKey = ref<string | null>(null);

const synthesisOpen = ref(false);
const synthesisTopic = ref("");
const synthesisQuestion = ref("");
const synthesisTypeId = ref("");
const synthesisCreating = ref(false);
const synthesisError = ref<string | null>(null);
const synthesisDraft = ref<DraftMutation | null>(null);
const synthesisHandoff = ref<KnowledgeSynthesisHandoff | null>(null);
const synthesisIdempotencyKey = ref<string | null>(null);
const copyStatus = ref<"idle" | "copied" | "manual">("idle");

const activeItems = computed(
  () => page.value?.items.filter((item) => item.status === "active") ?? [],
);
const selectedIds = computed(() =>
  selectedKnowledge.value.map((item) => item.knowledgeId),
);
const selectedSet = computed(() => new Set(selectedIds.value));
const enabledTypes = computed(() =>
  (model.value?.knowledgeTypes ?? [])
    .filter((type) => type.isEnabled && type.typeId)
    .sort(
      (left, right) =>
        left.sortOrder - right.sortOrder || left.key.localeCompare(right.key),
    ),
);
const defaultSynthesisTypeId = computed(
  () =>
    enabledTypes.value.find((type) => type.key === "article")?.typeId ??
    enabledTypes.value[0]?.typeId ??
    "",
);
const topicLength = computed(() => Array.from(topic.value.trim()).length);
const synthesisTopicLength = computed(
  () => Array.from(synthesisTopic.value.trim()).length,
);
const synthesisQuestionLength = computed(
  () => Array.from(synthesisQuestion.value.trim()).length,
);
const canCreate = computed(
  () =>
    selectedIds.value.length >= 1 &&
    selectedIds.value.length <= maxSelection &&
    topicLength.value >= 1 &&
    topicLength.value <= maxTopicLength &&
    !creating.value,
);
const canCreateSynthesis = computed(
  () =>
    selectedIds.value.length >= 1 &&
    selectedIds.value.length <= maxSelection &&
    synthesisTopicLength.value >= 1 &&
    synthesisTopicLength.value <= maxTopicLength &&
    synthesisQuestionLength.value <= maxQuestionLength &&
    enabledTypes.value.some((type) => type.typeId === synthesisTypeId.value) &&
    !synthesisCreating.value,
);
const synthesisTaskText = computed(() => {
  const handoff = synthesisHandoff.value;
  if (!handoff) {
    return "";
  }
  const knowledgeLines = handoff.knowledge
    .map((item, index) => `${index + 1}. ${item.title} (${item.knowledgeId})`)
    .join("\n");
  return `请使用 CipherVault MCP Prompt \`synthesize_selected_knowledge\` 完成知识综合。

综合主题：${handoff.topic}
综合问题：${handoff.question}
目标 Knowledge 类型：${handoff.knowledgeTypeName} (${handoff.knowledgeTypeKey}, ${handoff.knowledgeTypeId})

所选 Knowledge：
${knowledgeLines}

要求：
- 先读取 Knowledge Model 和每条 Knowledge 的当前内容与来源依据。
- 比较共同观点、分歧、假设、证据质量和适用条件。
- 保留全部 material SourceReference，不编造来源或定位。
- 区分来源作者观点、用户观点、事实与推断。
- 最终通过 create_draft 或 update_draft 创建/修改 Draft。
- MCP 是 Draft-only；提交审核，不得 apply、approve 或 reject。`;
});

const load = async () => {
  loading.value = true;
  error.value = null;
  try {
    page.value = query.value.trim()
      ? ((await knowledgeBaseApi.searchKnowledge({
          query: query.value,
          mode: mode.value,
          pageSize: 50,
        })) as PagedResult<KnowledgeSummary>)
      : await knowledgeBaseApi.knowledge({ status: "active", pageSize: 50 });
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "加载失败";
  } finally {
    loading.value = false;
  }
};

const loadModel = async () => {
  modelError.value = null;
  try {
    model.value = await knowledgeBaseApi.knowledgeModel();
  } catch (caught) {
    modelError.value =
      caught instanceof Error ? caught.message : "知识模型加载失败";
  }
};

const toggleSelection = (item: KnowledgeSummary) => {
  if (selectedSet.value.has(item.knowledgeId)) {
    selectedKnowledge.value = selectedKnowledge.value.filter(
      (selected) => selected.knowledgeId !== item.knowledgeId,
    );
    return;
  }
  if (selectedKnowledge.value.length < maxSelection) {
    selectedKnowledge.value = [...selectedKnowledge.value, item];
  }
};

const clearSelection = () => {
  selectedKnowledge.value = [];
};

const openRetrospective = () => {
  if (selectedIds.value.length === 0) {
    return;
  }
  retrospectiveOpen.value = true;
  createError.value = null;
  createdDraft.value = null;
  submissionKey.value = null;
};

const closeRetrospective = () => {
  if (!creating.value) {
    retrospectiveOpen.value = false;
  }
};

const openSynthesis = () => {
  if (selectedIds.value.length === 0 || enabledTypes.value.length === 0) {
    return;
  }
  if (
    !enabledTypes.value.some((type) => type.typeId === synthesisTypeId.value)
  ) {
    synthesisTypeId.value = defaultSynthesisTypeId.value;
  }
  synthesisOpen.value = true;
  synthesisError.value = null;
  synthesisDraft.value = null;
  synthesisHandoff.value = null;
  synthesisIdempotencyKey.value = null;
  copyStatus.value = "idle";
};

const closeSynthesis = () => {
  if (!synthesisCreating.value) {
    synthesisOpen.value = false;
  }
};

const createRetrospective = async () => {
  if (!canCreate.value) {
    return;
  }
  creating.value = true;
  createError.value = null;
  submissionKey.value ??= crypto.randomUUID();
  try {
    createdDraft.value =
      await knowledgeBaseApi.createKnowledgeRetrospectiveDraft(
        {
          topic: topic.value.trim(),
          knowledgeIds: [...selectedIds.value],
        },
        submissionKey.value,
      );
  } catch (caught) {
    createError.value =
      caught instanceof Error ? caught.message : "创建复盘草稿失败";
  } finally {
    creating.value = false;
  }
};

const createSynthesis = async () => {
  if (!canCreateSynthesis.value) {
    return;
  }
  const type = enabledTypes.value.find(
    (item) => item.typeId === synthesisTypeId.value,
  );
  if (!type?.typeId) {
    return;
  }
  const topicSnapshot = synthesisTopic.value.trim();
  const questionSnapshot =
    synthesisQuestion.value.trim() || defaultSynthesisQuestion;
  const knowledgeSnapshot = selectedKnowledge.value.map((item) => ({
    knowledgeId: item.knowledgeId,
    title: item.title,
  }));
  synthesisCreating.value = true;
  synthesisError.value = null;
  copyStatus.value = "idle";
  synthesisIdempotencyKey.value ??= crypto.randomUUID();
  try {
    const draft = await knowledgeBaseApi.createKnowledgeSynthesisDraft(
      {
        topic: topicSnapshot,
        question: synthesisQuestion.value.trim() || null,
        knowledgeIds: knowledgeSnapshot.map((item) => item.knowledgeId),
        knowledgeTypeId: type.typeId,
      },
      synthesisIdempotencyKey.value,
    );
    synthesisDraft.value = draft;
    synthesisHandoff.value = {
      draftId: draft.draftId,
      topic: topicSnapshot,
      question: questionSnapshot,
      knowledgeTypeId: type.typeId,
      knowledgeTypeKey: type.key,
      knowledgeTypeName: type.name,
      knowledge: knowledgeSnapshot,
    };
  } catch (caught) {
    synthesisError.value =
      caught instanceof Error ? caught.message : "创建知识综合草稿失败";
  } finally {
    synthesisCreating.value = false;
  }
};

const copySynthesisTask = async () => {
  const text = synthesisTaskText.value;
  if (!text) {
    return;
  }
  if (!navigator.clipboard?.writeText) {
    copyStatus.value = "manual";
    return;
  }
  try {
    await navigator.clipboard.writeText(text);
    copyStatus.value = "copied";
  } catch {
    copyStatus.value = "manual";
  }
};

watch(topic, () => {
  submissionKey.value = null;
  createdDraft.value = null;
});
watch([synthesisTopic, synthesisQuestion, synthesisTypeId], () => {
  synthesisIdempotencyKey.value = null;
  synthesisDraft.value = null;
  synthesisHandoff.value = null;
  copyStatus.value = "idle";
});
watch(
  selectedKnowledge,
  () => {
    submissionKey.value = null;
    createdDraft.value = null;
    synthesisIdempotencyKey.value = null;
    synthesisDraft.value = null;
    synthesisHandoff.value = null;
    copyStatus.value = "idle";
  },
  { deep: true },
);

onMounted(async () => {
  await Promise.all([load(), loadModel()]);
});

defineExpose({ selectedIds });
</script>

<template>
  <section class="page">
    <header class="page-header">
      <div>
        <p class="eyebrow">Knowledge</p>
        <h1>知识库</h1>
      </div>
      <RouterLink class="button" to="/inbox">新建知识草稿</RouterLink>
    </header>
    <form class="search" @submit.prevent="load">
      <input v-model="query" placeholder="搜索标题、标签或正文" />
      <select v-model="mode" aria-label="检索模式">
        <option value="keyword">关键词</option>
        <option value="fulltext">全文</option>
      </select>
      <button type="submit" :disabled="loading">
        {{ loading ? "检索中" : "搜索" }}
      </button>
    </form>
    <p v-if="error" class="error">{{ error }}</p>
    <p v-if="modelError" class="error">{{ modelError }}</p>
    <p v-if="model && enabledTypes.length === 0" class="error">
      知识模型没有启用中的知识类型。
    </p>
    <div v-if="selectedIds.length" class="selection-bar">
      <span>已选择 {{ selectedIds.length }} / {{ maxSelection }} 条</span>
      <div class="selection-actions">
        <button type="button" @click="openRetrospective">生成复盘草稿</button>
        <button
          type="button"
          :disabled="enabledTypes.length === 0"
          @click="openSynthesis"
        >
          生成知识综合草稿
        </button>
        <button type="button" class="secondary" @click="clearSelection">
          清空
        </button>
      </div>
    </div>
    <div class="list">
      <div
        v-for="item in activeItems"
        :key="item.knowledgeId"
        class="knowledge-row"
        :class="{ selected: selectedSet.has(item.knowledgeId) }"
      >
        <label class="selection">
          <input
            type="checkbox"
            :checked="selectedSet.has(item.knowledgeId)"
            :disabled="
              !selectedSet.has(item.knowledgeId) &&
              selectedIds.length >= maxSelection
            "
            :aria-label="`选择 ${item.title}`"
            @change="toggleSelection(item)"
          />
        </label>
        <RouterLink
          class="knowledge-link"
          :to="`/knowledge/${item.knowledgeId}`"
        >
          <div>
            <h2>{{ item.title }}</h2>
            <p>{{ item.summary || "暂无摘要" }}</p>
          </div>
          <div class="meta">
            <span>{{ item.knowledgeTypeKey }}</span>
            <span>v{{ item.revision }}</span>
          </div>
        </RouterLink>
      </div>
      <p v-if="!loading && activeItems.length === 0" class="muted">
        没有匹配的知识。
      </p>
    </div>

    <div
      v-if="retrospectiveOpen"
      class="modal-backdrop"
      @click.self="closeRetrospective"
    >
      <section
        class="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="retrospective-title"
      >
        <header class="modal-header">
          <div>
            <p class="eyebrow">Reflection draft</p>
            <h2 id="retrospective-title">生成复盘草稿</h2>
          </div>
          <button
            type="button"
            class="icon-button"
            aria-label="关闭"
            :disabled="creating"
            @click="closeRetrospective"
          >
            ×
          </button>
        </header>
        <p class="muted">
          将按选择顺序引用 {{ selectedIds.length }} 条知识，并保留来源依据。
        </p>
        <form @submit.prevent="createRetrospective">
          <label class="field">
            <span>复盘主题</span>
            <input
              v-model="topic"
              autofocus
              placeholder="例如：v1.3 P0 交付复盘"
            />
          </label>
          <div class="field-meta">
            <span>1～{{ maxTopicLength }} 个字符</span>
            <span>{{ topicLength }} / {{ maxTopicLength }}</span>
          </div>
          <p v-if="createError" class="error" role="alert">
            {{ createError }}
          </p>
          <div v-if="createdDraft" class="success" role="status">
            <p>复盘草稿已创建。</p>
            <RouterLink
              class="button"
              :to="{
                path: '/review',
                query: { status: 'draft', draftId: createdDraft.draftId },
              }"
            >
              打开复盘草稿
            </RouterLink>
          </div>
          <div class="modal-actions">
            <button type="submit" :disabled="!canCreate">
              {{ creating ? "创建中" : "创建草稿" }}
            </button>
            <button
              type="button"
              class="secondary"
              :disabled="creating"
              @click="closeRetrospective"
            >
              取消
            </button>
          </div>
        </form>
      </section>
    </div>

    <div
      v-if="synthesisOpen"
      class="modal-backdrop"
      @click.self="closeSynthesis"
    >
      <section
        class="modal synthesis-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="synthesis-title"
      >
        <header class="modal-header">
          <div>
            <p class="eyebrow">Knowledge synthesis</p>
            <h2 id="synthesis-title">生成知识综合草稿</h2>
          </div>
          <button
            type="button"
            class="icon-button"
            aria-label="关闭"
            :disabled="synthesisCreating"
            @click="closeSynthesis"
          >
            ×
          </button>
        </header>
        <p class="muted">
          将按选择顺序引用 {{ selectedIds.length }} 条知识，并继承来源依据。
        </p>
        <form @submit.prevent="createSynthesis">
          <label class="field">
            <span>目标知识类型</span>
            <select v-model="synthesisTypeId">
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
            <span>综合主题</span>
            <input
              v-model="synthesisTopic"
              autofocus
              placeholder="例如：跨来源证据综合"
            />
          </label>
          <div class="field-meta">
            <span>1～{{ maxTopicLength }} 个字符</span>
            <span>{{ synthesisTopicLength }} / {{ maxTopicLength }}</span>
          </div>
          <label class="field">
            <span>综合问题</span>
            <textarea
              v-model="synthesisQuestion"
              rows="4"
              placeholder="可选。留空时使用默认综合说明。"
            />
          </label>
          <div class="field-meta">
            <span>最多 {{ maxQuestionLength }} 个字符</span>
            <span>
              {{ synthesisQuestionLength }} / {{ maxQuestionLength }}
            </span>
          </div>
          <p v-if="synthesisError" class="error" role="alert">
            {{ synthesisError }}
          </p>
          <div v-if="synthesisHandoff" class="success synthesis-success">
            <p>知识综合草稿已创建。</p>
            <div class="success-actions">
              <RouterLink
                class="button"
                :to="{
                  path: '/review',
                  query: {
                    status: 'draft',
                    draftId: synthesisHandoff.draftId,
                  },
                }"
              >
                打开综合草稿
              </RouterLink>
              <button type="button" @click="copySynthesisTask">
                复制外部 AI 综合任务
              </button>
            </div>
            <p v-if="copyStatus === 'copied'" class="copy-status">已复制。</p>
            <p v-if="copyStatus === 'manual'" class="copy-status">
              无法自动复制，请手动选择下面文本。
            </p>
            <textarea
              class="handoff-text"
              readonly
              :value="synthesisTaskText"
              aria-label="外部 AI 综合任务"
            />
          </div>
          <div class="modal-actions">
            <button type="submit" :disabled="!canCreateSynthesis">
              {{ synthesisCreating ? "创建中" : "创建综合草稿" }}
            </button>
            <button
              type="button"
              class="secondary"
              :disabled="synthesisCreating"
              @click="closeSynthesis"
            >
              取消
            </button>
          </div>
        </form>
      </section>
    </div>
  </section>
</template>

<style scoped>
.page {
  padding: 28px;
}
.page-header {
  display: flex;
  align-items: end;
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
  margin: 0 0 18px;
  font-size: 28px;
}
.button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: 9px 14px;
  border-radius: 7px;
  background: var(--cv-accent);
  color: white;
  text-decoration: none;
}
.search {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 130px auto;
  gap: 8px;
  margin-bottom: 16px;
}
input,
select,
textarea,
button {
  min-height: 40px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-surface);
  color: var(--cv-text);
  padding: 0 12px;
}
button {
  cursor: pointer;
}
button:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}
.selection-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 12px;
  padding: 10px 12px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  color: var(--cv-muted);
  font-size: 13px;
}
.selection-actions {
  display: flex;
  gap: 8px;
}
.list {
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  overflow: hidden;
}
.knowledge-row {
  display: flex;
  align-items: stretch;
  border-bottom: 1px solid var(--cv-border);
}
.knowledge-row:last-child {
  border-bottom: 0;
}
.knowledge-row.selected {
  background: color-mix(in srgb, var(--cv-accent) 7%, var(--cv-surface));
}
.selection {
  display: flex;
  align-items: center;
  padding-left: 14px;
}
.selection input {
  width: 18px;
  min-height: 18px;
  padding: 0;
}
.knowledge-link {
  display: flex;
  flex: 1;
  justify-content: space-between;
  gap: 20px;
  min-width: 0;
  padding: 16px;
  color: inherit;
  text-decoration: none;
}
h2 {
  margin: 0;
  font-size: 15px;
}
.knowledge-link p {
  margin: 5px 0 0;
  color: var(--cv-muted);
  font-size: 13px;
}
.meta {
  display: flex;
  gap: 8px;
  color: var(--cv-muted);
  font-size: 12px;
  white-space: nowrap;
}
.muted {
  color: var(--cv-muted);
}
.error {
  color: #b93f36;
}
.modal-backdrop {
  position: fixed;
  inset: 0;
  z-index: 40;
  display: grid;
  place-items: center;
  padding: 20px;
  background: rgb(0 0 0 / 45%);
}
.modal {
  width: min(560px, 100%);
  padding: 20px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  color: var(--cv-text);
  box-shadow: 0 18px 60px rgb(0 0 0 / 30%);
}
.modal-header {
  display: flex;
  align-items: start;
  justify-content: space-between;
  gap: 16px;
}
.modal-header h2 {
  font-size: 20px;
}
.icon-button {
  width: 36px;
  min-height: 36px;
  padding: 0;
  font-size: 22px;
  line-height: 1;
}
.field {
  display: grid;
  gap: 6px;
  margin-top: 16px;
  font-size: 13px;
}
.field input {
  width: 100%;
}
.field select,
.field textarea {
  width: 100%;
  min-height: 40px;
  padding: 10px 12px;
  font: inherit;
}
.synthesis-modal {
  width: min(760px, 100%);
  max-height: 90vh;
  overflow: auto;
}
.field-meta {
  display: flex;
  justify-content: space-between;
  margin-top: 5px;
  color: var(--cv-muted);
  font-size: 12px;
}
.modal-actions {
  display: flex;
  gap: 8px;
  margin-top: 18px;
}
.secondary {
  border: 1px solid var(--cv-border);
  background: transparent;
  color: var(--cv-text);
}
.success {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-top: 14px;
  padding: 12px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
}
.success p {
  margin: 0;
}
.synthesis-success {
  display: grid;
  align-items: stretch;
}
.success-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.copy-status {
  color: var(--cv-muted);
  font-size: 12px;
}
.handoff-text {
  width: 100%;
  min-height: 220px;
  padding: 10px 12px;
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  font-size: 12px;
  line-height: 1.5;
  resize: vertical;
}
@media (max-width: 700px) {
  .page {
    padding: 18px;
  }
  .search {
    grid-template-columns: 1fr;
  }
  .page-header {
    align-items: start;
    flex-direction: column;
  }
  .selection-bar,
  .success {
    align-items: stretch;
    flex-direction: column;
  }
  .knowledge-link {
    gap: 8px;
    padding: 14px;
  }
  .meta {
    flex-direction: column;
  }
}
</style>
