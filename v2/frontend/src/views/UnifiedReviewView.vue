<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { useRoute, useRouter } from "vue-router";
import type { LocationQueryRaw } from "vue-router";
import { knowledgeBaseApi } from "../api/knowledge-base";
import type { DraftDetail, DraftListItem } from "../types/knowledge-base";

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
const message = ref("");
const status = ref(initialStatus);

const load = async () => {
  drafts.value = (
    await knowledgeBaseApi.drafts({ status: status.value, pageSize: 100 })
  ).items;
  selected.value = null;
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

const openById = async (draftId: string) => {
  selected.value = await knowledgeBaseApi.draft(draftId);
  const selectedStatus = selected.value.record.status;
  if (
    allowedStatuses.includes(selectedStatus as (typeof allowedStatuses)[number])
  ) {
    status.value = selectedStatus;
  }
  await syncQuery(draftId);
};

const open = async (item: DraftListItem) => {
  message.value = "";
  await openById(item.draftId);
};

const changeStatus = async () => {
  message.value = "";
  await load();
  await syncQuery(null);
};

const apply = async () => {
  if (!selected.value) return;
  await knowledgeBaseApi.applyDraft(
    selected.value.record.draftId,
    selected.value.record.draftRevision,
  );
  await load();
  await syncQuery(null);
};

const submitForReview = async () => {
  if (!selected.value) return;
  await knowledgeBaseApi.submitDraft(
    selected.value.record.draftId,
    selected.value.record.draftRevision,
  );
  await load();
  await syncQuery(null);
};

const reject = async () => {
  if (!selected.value) return;
  await knowledgeBaseApi.rejectDraft(
    selected.value.record.draftId,
    selected.value.record.draftRevision,
  );
  await load();
  await syncQuery(null);
};

const pendingCount = computed(() => drafts.value.length);
onMounted(async () => {
  await load();
  if (initialDraftId) {
    try {
      await openById(initialDraftId);
    } catch (caught) {
      message.value =
        caught instanceof Error ? caught.message : "无法打开指定草稿";
    }
  }
});
</script>

<template>
  <section class="page">
    <header class="page-header">
      <div>
        <p class="eyebrow">Review</p>
        <h1>AI 工作区</h1>
        <p class="muted">统一审核 Knowledge、Source 和关系变更。</p>
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
          <span>{{ item.operationType }} · {{ item.sourceActor }}</span>
        </button>
      </aside>
      <main v-if="selected">
        <h2>{{ selected.preview.summary }}</h2>
        <dl>
          <dt>目标</dt>
          <dd>{{ selected.record.targetObjectType }}</dd>
          <dt>操作</dt>
          <dd>{{ selected.record.operationType }}</dd>
          <dt>状态</dt>
          <dd>{{ selected.record.status }}</dd>
          <dt>模型版本</dt>
          <dd>{{ selected.record.baseModelVersion }}</dd>
        </dl>
        <pre tabindex="0">{{ selected.preview.diff }}</pre>
        <section v-if="selected.preview.sourceReferences.length">
          <h3>来源依据</h3>
          <blockquote
            v-for="sourceRef in selected.preview.sourceReferences"
            :key="sourceRef.sourceReferenceId || sourceRef.sourceId"
          >
            {{ sourceRef.excerpt }}
          </blockquote>
        </section>
        <div v-if="selected.record.status === 'pending_review'" class="actions">
          <button type="button" @click="apply">批准并写入</button>
          <button type="button" class="secondary" @click="reject">拒绝</button>
        </div>
        <div v-if="selected.record.status === 'draft'" class="actions">
          <button type="button" @click="submitForReview">提交审核</button>
        </div>
        <p>{{ message }}</p>
      </main>
      <main v-else class="empty">选择一条 Draft 查看详情。</main>
    </div>
  </section>
</template>

<style scoped>
.page {
  padding: 28px;
}
.page-header {
  display: flex;
  justify-content: space-between;
  gap: 16px;
  align-items: end;
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
dl {
  display: grid;
  grid-template-columns: 110px 1fr;
  gap: 6px;
}
dt {
  color: var(--cv-muted);
}
pre {
  max-height: 380px;
  overflow: auto;
  padding: 12px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  white-space: pre-wrap;
}
blockquote {
  margin: 8px 0;
  padding-left: 12px;
  border-left: 2px solid var(--cv-accent);
  color: var(--cv-muted);
}
.actions {
  display: flex;
  gap: 8px;
  margin-top: 14px;
}
button {
  padding: 9px 14px;
  border: 0;
  border-radius: 7px;
  background: var(--cv-accent);
  color: white;
  cursor: pointer;
}
button.secondary {
  background: var(--cv-surface);
  border: 1px solid var(--cv-border);
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
}
</style>
