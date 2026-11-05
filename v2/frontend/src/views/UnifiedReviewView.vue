<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import { onBeforeRouteLeave, useRoute, useRouter } from "vue-router";
import type { LocationQueryRaw } from "vue-router";
import { knowledgeBaseApi } from "../api/knowledge-base";
import type {
  DraftDetail,
  DraftListItem,
  KnowledgeModel,
} from "../types/knowledge-base";
import type {
  EditableDraftObjectType,
  EditableDraftPayload,
  KnowledgeDraftPayload,
  RelationDraftPayload,
  SourceDraftPayload,
} from "../types/draft-editor";
import { ApiClientError } from "../types/domain";
import {
  cloneDraftPayload,
  isDraftPayloadDirty,
  parseDraftPayload,
  serializeDraftPayload,
} from "../utils/draft-editor";
import DraftChangeList from "../components/draft/DraftChangeList.vue";
import DraftJsonPreview from "../components/draft/DraftJsonPreview.vue";
import DraftKnowledgeEditor from "../components/draft/DraftKnowledgeEditor.vue";
import DraftRelationEditor from "../components/draft/DraftRelationEditor.vue";
import DraftSourceEditor from "../components/draft/DraftSourceEditor.vue";
import DraftValidationList from "../components/draft/DraftValidationList.vue";

const allowedStatuses = [
  "pending_review",
  "draft",
  "conflict",
  "applied",
] as const;

const route = useRoute();
const router = useRouter();
const initialStatus =
  typeof route.query.status === "string" &&
  allowedStatuses.includes(
    route.query.status as (typeof allowedStatuses)[number],
  )
    ? route.query.status
    : "pending_review";
const initialDraftId =
  typeof route.query.draftId === "string" ? route.query.draftId : null;

const drafts = ref<DraftListItem[]>([]);
const selected = ref<DraftDetail | null>(null);
const model = ref<KnowledgeModel | null>(null);
const message = ref("");
const error = ref<string | null>(null);
const status = ref(initialStatus);
const form = ref<EditableDraftPayload | null>(null);
const baseline = ref<EditableDraftPayload | null>(null);
const saving = ref(false);
const saveError = ref<string | null>(null);
const saveIdempotencyKey = ref<string | null>(null);
const revisionConflict = ref(false);
const modelLoading = ref(true);

const targetType = computed<EditableDraftObjectType | null>(() => {
  const value = selected.value?.record.targetObjectType;
  return value === "knowledge" || value === "source" || value === "relation"
    ? value
    : null;
});
const operationEditable = computed(
  () =>
    selected.value?.record.operationType === "create" ||
    selected.value?.record.operationType === "update",
);
const canEdit = computed(
  () =>
    !!selected.value &&
    operationEditable.value &&
    (selected.value.record.status === "draft" ||
      selected.value.record.status === "conflict" ||
      selected.value.record.status === "pending_review"),
);
const dirty = computed(() =>
  targetType.value
    ? isDraftPayloadDirty(targetType.value, baseline.value, form.value)
    : false,
);
const issues = computed(() => selected.value?.preview.validationIssues ?? []);
const knowledgePayload = computed(() =>
  targetType.value === "knowledge"
    ? (form.value as KnowledgeDraftPayload | null)
    : null,
);
const sourcePayload = computed(() =>
  targetType.value === "source"
    ? (form.value as SourceDraftPayload | null)
    : null,
);
const relationPayload = computed(() =>
  targetType.value === "relation"
    ? (form.value as RelationDraftPayload | null)
    : null,
);

const load = async () => {
  drafts.value = (
    await knowledgeBaseApi.drafts({ status: status.value, pageSize: 100 })
  ).items;
  selected.value = null;
  form.value = null;
  baseline.value = null;
};

const loadModel = async () => {
  modelLoading.value = true;
  try {
    model.value = await knowledgeBaseApi.knowledgeModel();
  } catch (caught) {
    saveError.value =
      caught instanceof Error ? caught.message : "知识模型加载失败";
  } finally {
    modelLoading.value = false;
  }
};

const syncQuery = async (draftId: string | null) => {
  const nextQuery: LocationQueryRaw = {
    ...route.query,
    status: status.value,
  };
  if (draftId) {
    nextQuery.draftId = draftId;
  } else {
    delete nextQuery.draftId;
  }
  await router.replace({ query: nextQuery });
};

const setSelected = (detail: DraftDetail) => {
  selected.value = detail;
  saveError.value = null;
  revisionConflict.value = false;
  saveIdempotencyKey.value = null;
  const type = detail.record.targetObjectType;
  const editableOperation =
    detail.record.operationType === "create" ||
    detail.record.operationType === "update";
  if (
    editableOperation &&
    (type === "knowledge" || type === "source" || type === "relation")
  ) {
    const parsed = parseDraftPayload(
      type,
      detail.record.payload as Record<string, unknown>,
    );
    form.value = cloneDraftPayload(parsed);
    baseline.value = cloneDraftPayload(parsed);
  } else {
    form.value = null;
    baseline.value = null;
  }
};

const openById = async (draftId: string) => {
  const detail = await knowledgeBaseApi.draft(draftId);
  setSelected(detail);
  const selectedStatus = detail.record.status;
  if (
    allowedStatuses.includes(selectedStatus as (typeof allowedStatuses)[number])
  ) {
    status.value = selectedStatus;
  }
  await syncQuery(draftId);
};

const confirmDiscard = (): boolean =>
  !dirty.value || window.confirm("有未保存的修改，确定放弃吗？");

const open = async (item: DraftListItem) => {
  if (!confirmDiscard()) {
    return;
  }
  message.value = "";
  await openById(item.draftId);
};

const changeStatus = async () => {
  if (!confirmDiscard()) {
    return;
  }
  message.value = "";
  await load();
  await syncQuery(null);
};

const updateForm = (value: EditableDraftPayload) => {
  form.value = value;
  saveIdempotencyKey.value = null;
  saveError.value = null;
  revisionConflict.value = false;
};

const save = async () => {
  if (
    !selected.value ||
    !targetType.value ||
    !form.value ||
    !dirty.value ||
    !canEdit.value
  ) {
    return;
  }
  saving.value = true;
  saveError.value = null;
  revisionConflict.value = false;
  saveIdempotencyKey.value ??= crypto.randomUUID();
  try {
    const result = await knowledgeBaseApi.updateDraft(
      selected.value.record.draftId,
      selected.value.record.draftRevision,
      serializeDraftPayload(targetType.value, form.value),
      saveIdempotencyKey.value,
    );
    message.value =
      selected.value.record.status === "pending_review"
        ? "修改已保存，Draft 已退回处理中，请重新送审。"
        : "Draft 已保存。";
    status.value = result.status;
    await load();
    await openById(result.draftId);
  } catch (caught) {
    saveError.value = caught instanceof Error ? caught.message : "保存失败";
    if (caught instanceof ApiClientError && caught.httpStatus === 409) {
      revisionConflict.value = caught.code === "draft_revision_conflict";
    }
  } finally {
    saving.value = false;
  }
};

const reload = async () => {
  if (!selected.value || !confirmDiscard()) {
    return;
  }
  await openById(selected.value.record.draftId);
};

const submitForReview = async () => {
  if (!selected.value || dirty.value) return;
  const draftId = selected.value.record.draftId;
  await knowledgeBaseApi.submitDraft(
    draftId,
    selected.value.record.draftRevision,
  );
  status.value = "pending_review";
  await load();
  await openById(draftId);
};

const apply = async () => {
  if (!selected.value || dirty.value) return;
  await knowledgeBaseApi.applyDraft(
    selected.value.record.draftId,
    selected.value.record.draftRevision,
  );
  await load();
  await syncQuery(null);
};

const reject = async () => {
  if (!selected.value || dirty.value) return;
  await knowledgeBaseApi.rejectDraft(
    selected.value.record.draftId,
    selected.value.record.draftRevision,
  );
  await load();
  await syncQuery(null);
};

const discard = async () => {
  if (!selected.value || dirty.value) return;
  await knowledgeBaseApi.discardDraft(
    selected.value.record.draftId,
    selected.value.record.draftRevision,
  );
  await load();
  await syncQuery(null);
};

const pendingCount = computed(() => drafts.value.length);

const handleBeforeUnload = (event: BeforeUnloadEvent) => {
  if (!dirty.value) {
    return;
  }
  event.preventDefault();
  event.returnValue = "";
};

onBeforeRouteLeave(() => confirmDiscard());

onMounted(async () => {
  window.addEventListener("beforeunload", handleBeforeUnload);
  await Promise.all([load(), loadModel()]);
  if (initialDraftId) {
    try {
      await openById(initialDraftId);
    } catch (caught) {
      error.value =
        caught instanceof Error ? caught.message : "无法打开指定草稿";
    }
  }
});

onBeforeUnmount(() => {
  window.removeEventListener("beforeunload", handleBeforeUnload);
});
</script>

<template>
  <section class="page">
    <header class="page-header">
      <div>
        <p class="eyebrow">Review</p>
        <h1>AI 工作区</h1>
        <p class="muted">完善 Draft，保存后由你决定是否提交审核。</p>
      </div>
      <select v-model="status" aria-label="草稿状态" @change="changeStatus">
        <option value="pending_review">待审核</option>
        <option value="draft">处理中</option>
        <option value="conflict">冲突</option>
        <option value="applied">历史</option>
      </select>
    </header>
    <div class="layout">
      <aside aria-label="待审核草稿">
        <div class="count">{{ pendingCount }} 条</div>
        <button
          v-for="item in drafts"
          :key="item.draftId"
          class="draft-item"
          type="button"
          @click="open(item)"
        >
          <strong>{{ item.title || item.targetObjectType }}</strong>
          <span>
            {{ item.operationType }} · {{ item.sourceActor }} ·
            {{ item.draftRevision }}
          </span>
        </button>
      </aside>

      <main v-if="selected">
        <header class="draft-header">
          <div>
            <p class="eyebrow">{{ selected.record.targetObjectType }}</p>
            <h2>{{ selected.preview.summary }}</h2>
          </div>
          <div class="badges">
            <span>{{ selected.record.operationType }}</span>
            <span>{{ selected.record.status }}</span>
            <span>rev {{ selected.record.draftRevision }}</span>
          </div>
        </header>

        <p v-if="message" class="success-message">{{ message }}</p>
        <p v-if="saveError" class="error" role="alert">{{ saveError }}</p>
        <div v-if="revisionConflict" class="conflict-box">
          <p>Draft 已在其他位置更新。重新加载会丢弃当前未保存修改。</p>
          <button type="button" @click="reload">重新加载</button>
        </div>

        <p v-if="modelLoading" class="muted">加载知识模型…</p>
        <p v-else-if="!model" class="error">知识模型不可用。</p>

        <div v-if="model && knowledgePayload">
          <DraftKnowledgeEditor
            :payload="knowledgePayload"
            :model="model"
            :readonly="!canEdit"
            :issues="issues"
            @update:payload="updateForm"
          />
        </div>
        <div v-else-if="model && sourcePayload">
          <DraftSourceEditor
            :payload="sourcePayload"
            :model="model"
            :readonly="!canEdit"
            :issues="issues"
            @update:payload="updateForm"
          />
        </div>
        <div v-else-if="model && relationPayload">
          <DraftRelationEditor
            :payload="relationPayload"
            :model="model"
            :readonly="!canEdit"
            :issues="issues"
            @update:payload="updateForm"
          />
        </div>
        <p v-else-if="!operationEditable" class="muted">
          此操作没有可编辑 payload。
        </p>

        <div class="actions">
          <button
            v-if="canEdit && form"
            type="button"
            :disabled="!dirty || saving"
            @click="save"
          >
            {{ saving ? "保存中" : "保存 Draft" }}
          </button>
          <button
            v-if="selected.record.status === 'draft'"
            type="button"
            :disabled="dirty || saving"
            @click="submitForReview"
          >
            提交审核
          </button>
          <template v-if="selected.record.status === 'pending_review'">
            <button type="button" :disabled="dirty || saving" @click="apply">
              批准并写入
            </button>
            <button
              type="button"
              class="secondary"
              :disabled="dirty || saving"
              @click="reject"
            >
              拒绝
            </button>
          </template>
          <button
            v-if="
              selected.record.status === 'draft' ||
              selected.record.status === 'conflict'
            "
            type="button"
            class="secondary"
            :disabled="dirty || saving"
            @click="discard"
          >
            丢弃
          </button>
          <span v-if="dirty" class="dirty-label">未保存</span>
        </div>

        <DraftValidationList :issues="issues" />
        <DraftChangeList
          :changes="selected.preview.changes"
          :truncated="selected.preview.changesTruncated"
        />
        <DraftJsonPreview
          :json="selected.preview.diff"
          :truncated="selected.preview.diffTruncated"
        />
      </main>
      <main v-else class="empty">
        <p v-if="error" class="error">{{ error }}</p>
        <p>选择一条 Draft 查看详情。</p>
      </main>
    </div>
  </section>
</template>

<style scoped>
.page {
  padding: 28px;
}
.page-header,
.draft-header {
  display: flex;
  justify-content: space-between;
  gap: 16px;
}
.page-header {
  align-items: end;
}
.draft-header {
  align-items: start;
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
  font-size: 20px;
}
.muted {
  color: var(--cv-muted);
}
select {
  min-height: 38px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-surface);
  color: var(--cv-text);
}
.layout {
  display: grid;
  grid-template-columns: 320px minmax(0, 1fr);
  gap: 16px;
  margin-top: 18px;
}
aside {
  display: grid;
  align-content: start;
  gap: 6px;
}
aside button {
  display: grid;
  gap: 4px;
  padding: 12px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  color: inherit;
  cursor: pointer;
  text-align: left;
}
aside span,
.count {
  color: var(--cv-muted);
  font-size: 12px;
}
main {
  min-width: 0;
  padding: 18px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}
.badges,
.actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.badges span {
  padding: 4px 7px;
  border: 1px solid var(--cv-border);
  border-radius: 999px;
  color: var(--cv-muted);
  font-size: 11px;
}
.success-message {
  color: #2e7d4f;
}
.error,
.dirty-label {
  color: #b93f36;
}
.conflict-box {
  margin: 12px 0;
  padding: 12px;
  border: 1px solid #d7a7a1;
  border-radius: 7px;
  background: #fff4f2;
  color: #7d2f28;
}
.actions {
  align-items: center;
  margin-top: 18px;
}
button {
  padding: 9px 14px;
  border: 0;
  border-radius: 7px;
  background: var(--cv-accent);
  color: white;
  cursor: pointer;
}
button:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}
button.secondary {
  border: 1px solid var(--cv-border);
  background: transparent;
  color: var(--cv-text);
}
.empty {
  color: var(--cv-muted);
}
@media (max-width: 800px) {
  .page {
    padding: 18px;
  }
  .layout {
    grid-template-columns: 1fr;
  }
  .page-header,
  .draft-header {
    align-items: stretch;
    flex-direction: column;
  }
}
</style>
