<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from "vue";
import { useRouter } from "vue-router";
import {
  NAlert,
  NButton,
  NEmpty,
  NIcon,
  NPopconfirm,
  NSelect,
  NSpin,
  NTag,
  NTooltip,
  useMessage,
} from "naive-ui";
import { Check, ExternalLink, RefreshCw, ShieldAlert, X } from "@lucide/vue";
import { registerSensitiveStore } from "../../session/lifecycle";
import { useRelationReviewsStore } from "../../stores/relation-reviews";
import type { RelationSuggestionStatus, RelationType } from "../../types/api";

const reviews = useRelationReviewsStore();
const router = useRouter();
const message = useMessage();
let unregisterSensitiveStore: (() => void) | null = null;

const statusOptions: Array<{
  label: string;
  value: RelationSuggestionStatus;
}> = [
  { label: "待审核", value: "pending_review" },
  { label: "需要更新", value: "stale" },
  { label: "已应用", value: "applied" },
  { label: "已拒绝", value: "rejected" },
  { label: "已过期", value: "expired" },
];

const relationTypeOptions: Array<{
  label: string;
  value: RelationType;
}> = [
  { label: "相关", value: "related" },
  { label: "案例", value: "example_of" },
  { label: "实现", value: "implements" },
  { label: "对比", value: "contrasts_with" },
  { label: "依赖", value: "depends_on" },
];

const relationTypeLabels: Record<RelationType, string> = {
  related: "相关",
  example_of: "案例",
  implements: "实现",
  contrasts_with: "对比",
  depends_on: "依赖",
};
const symmetricRelationTypes = new Set<RelationType>([
  "related",
  "contrasts_with",
]);

const statusLabels: Record<RelationSuggestionStatus, string> = {
  pending_review: "待审核",
  stale: "需要更新",
  applied: "已应用",
  rejected: "已拒绝",
  expired: "已过期",
};

const staleReasonLabels: Record<string, string> = {
  source_revision_changed: "源文档版本已变化",
  target_revision_changed: "目标文档版本已变化",
  source_and_target_revision_changed: "源和目标文档版本均已变化",
  source_trashed: "源文档已进入回收站",
  target_trashed: "目标文档已进入回收站",
  source_and_target_trashed: "源和目标文档均已进入回收站",
  source_missing: "源文档不存在",
  target_missing: "目标文档不存在",
};

const selected = computed(() => reviews.selected);
const canReview = computed(
  () => selected.value?.metadata.status === "pending_review",
);
const submitter = computed(() => {
  const detail = selected.value;
  if (!detail) {
    return [];
  }
  return [
    detail.modelName ? `模型：${detail.modelName}` : null,
    detail.agentName ? `Agent：${detail.agentName}` : null,
    detail.clientName ? `客户端：${detail.clientName}` : null,
  ].filter((value): value is string => value !== null);
});

const load = async (): Promise<void> => {
  await reviews.refresh();
};

const select = async (suggestionId: string): Promise<void> => {
  await reviews.select(suggestionId);
};

const changeStatus = async (value: RelationSuggestionStatus): Promise<void> => {
  await reviews.setStatus(value);
};

const changeRelationType = async (
  value: RelationType | null,
): Promise<void> => {
  await reviews.setRelationType(value);
};

const apply = async (): Promise<void> => {
  if (await reviews.applySelected()) {
    message.success("关系建议已应用。");
  } else {
    message.error(reviews.error?.message ?? "应用关系建议失败。");
  }
};

const reject = async (): Promise<void> => {
  if (await reviews.rejectSelected()) {
    message.success("关系建议已拒绝。");
  } else {
    message.error(reviews.error?.message ?? "拒绝关系建议失败。");
  }
};

const openDocument = async (documentId: string): Promise<void> => {
  await router.push({ name: "document", params: { id: documentId } });
};

onMounted(() => {
  unregisterSensitiveStore = registerSensitiveStore(reviews);
  void load();
});

onBeforeUnmount(() => {
  unregisterSensitiveStore?.();
  unregisterSensitiveStore = null;
  reviews.resetSensitive();
});
</script>

<template>
  <section class="relation-review" aria-label="关系建议审核">
    <div class="relation-summary">
      <div>
        <span>待审核</span>
        <strong>{{ reviews.summary?.pendingReview ?? "—" }}</strong>
      </div>
      <div>
        <span>需要更新</span>
        <strong>{{ reviews.summary?.stale ?? "—" }}</strong>
      </div>
      <div>
        <span>已应用</span>
        <strong>{{ reviews.summary?.applied ?? "—" }}</strong>
      </div>
      <div>
        <span>已拒绝</span>
        <strong>{{ reviews.summary?.rejected ?? "—" }}</strong>
      </div>
      <NTooltip trigger="hover">
        <template #trigger>
          <NButton
            quaternary
            circle
            aria-label="刷新关系建议"
            :loading="reviews.loading || reviews.summaryLoading"
            @click="load"
          >
            <template #icon>
              <NIcon :component="RefreshCw" aria-hidden="true" />
            </template>
          </NButton>
        </template>
        刷新关系建议
      </NTooltip>
    </div>

    <NAlert v-if="reviews.error" type="error" :show-icon="false">
      {{ reviews.error.message }}
    </NAlert>

    <div class="relation-filters">
      <NSelect
        :value="reviews.filters.status"
        :options="statusOptions"
        aria-label="关系建议状态"
        @update:value="changeStatus"
      />
      <NSelect
        :value="reviews.filters.relationType"
        :options="relationTypeOptions"
        clearable
        aria-label="关系类型"
        placeholder="全部关系类型"
        @update:value="changeRelationType"
      />
    </div>

    <div class="relation-layout">
      <section class="suggestion-list" aria-label="关系建议列表">
        <div v-if="reviews.loading && !reviews.items.length" class="loading">
          <NSpin size="small" />
          <span>正在读取关系建议…</span>
        </div>
        <NEmpty v-else-if="!reviews.items.length" description="暂无关系建议" />
        <template v-else>
          <button
            v-for="item in reviews.items"
            :key="item.suggestionId"
            type="button"
            :class="{
              active:
                reviews.selected?.metadata.suggestionId === item.suggestionId,
            }"
            @click="select(item.suggestionId)"
          >
            <span class="suggestion-title">
              {{ relationTypeLabels[item.relationType] }} ·
              {{ item.sourceDocument.title }}
              {{ symmetricRelationTypes.has(item.relationType) ? "↔" : "→" }}
              {{ item.targetDocument.title }}
            </span>
            <span>{{ item.sourceDocument.virtualPath }}</span>
            <span class="suggestion-meta">
              {{ statusLabels[item.status] }} · R{{ item.suggestionRevision }} ·
              {{ new Date(item.updatedAt).toLocaleString("zh-CN") }}
            </span>
          </button>
          <NButton
            v-if="reviews.hasMore"
            size="small"
            secondary
            :loading="reviews.loading"
            @click="reviews.load(true)"
          >
            加载更多
          </NButton>
        </template>
      </section>

      <section class="suggestion-detail">
        <div v-if="reviews.detailLoading" class="loading">
          <NSpin size="small" />
          <span>正在读取详情…</span>
        </div>
        <NEmpty v-else-if="!selected" description="选择一条关系建议查看详情" />
        <template v-else>
          <header class="detail-heading">
            <div>
              <div class="detail-tags">
                <NTag size="small" :bordered="false">
                  {{ relationTypeLabels[selected.metadata.relationType] }}
                </NTag>
                <NTag size="small" :bordered="false">
                  {{ statusLabels[selected.metadata.status] }}
                </NTag>
                <NTag size="small" :bordered="false">
                  R{{ selected.metadata.suggestionRevision }}
                </NTag>
              </div>
              <h2>{{ selected.metadata.sourceDocument.title }}</h2>
              <p>
                {{
                  symmetricRelationTypes.has(selected.metadata.relationType)
                    ? `与 ${selected.metadata.targetDocument.title} 相互关联`
                    : `指向 ${selected.metadata.targetDocument.title}`
                }}
              </p>
            </div>
          </header>

          <NAlert
            v-if="selected.metadata.status === 'stale'"
            type="warning"
            :show-icon="true"
          >
            <template #icon>
              <NIcon :component="ShieldAlert" aria-hidden="true" />
            </template>
            {{
              staleReasonLabels[selected.metadata.staleReasonCode ?? ""] ??
              "源或目标文档已变化"
            }}。需要 MCP 基于最新版本重建后才能应用。
          </NAlert>

          <section class="relation-documents">
            <button
              type="button"
              @click="openDocument(selected.metadata.sourceDocument.documentId)"
            >
              <span>源文档</span>
              <strong>{{ selected.metadata.sourceDocument.title }}</strong>
              <small>
                建议 R{{ selected.metadata.sourceDocumentRevision }} · 当前 R{{
                  selected.metadata.sourceDocument.revision
                }}
              </small>
              <NIcon :component="ExternalLink" aria-hidden="true" />
            </button>
            <button
              type="button"
              @click="openDocument(selected.metadata.targetDocument.documentId)"
            >
              <span>目标文档</span>
              <strong>{{ selected.metadata.targetDocument.title }}</strong>
              <small>
                建议 R{{ selected.metadata.targetDocumentRevision }} · 当前 R{{
                  selected.metadata.targetDocument.revision
                }}
              </small>
              <NIcon :component="ExternalLink" aria-hidden="true" />
            </button>
          </section>

          <section class="reason-section">
            <h3>建议理由</h3>
            <p>{{ selected.reason }}</p>
          </section>

          <section class="submitter-section">
            <header>
              <h3>提交方</h3>
              <span>提交方提供，未经核实</span>
            </header>
            <ul>
              <li v-for="item in submitter" :key="item">{{ item }}</li>
            </ul>
          </section>

          <section class="evidence-section">
            <header>
              <h3>证据</h3>
              <span>{{ selected.evidence.length }} / 3</span>
            </header>
            <ul v-if="selected.evidence.length">
              <li v-for="(item, index) in selected.evidence" :key="index">
                <strong>
                  {{
                    item.documentId ===
                    selected.metadata.sourceDocument.documentId
                      ? "源文档"
                      : "目标文档"
                  }}
                  · R{{ item.documentRevision }}
                </strong>
                <small v-if="item.headingPath.length">
                  {{ item.headingPath.join(" / ") }}
                </small>
                <blockquote>{{ item.excerpt }}</blockquote>
              </li>
            </ul>
            <NEmpty v-else description="没有证据引用" size="small" />
          </section>

          <footer v-if="canReview" class="detail-actions">
            <NPopconfirm @positive-click="apply">
              <template #trigger>
                <NButton
                  type="primary"
                  :loading="reviews.actionLoading"
                  :disabled="!canReview"
                >
                  <template #icon>
                    <NIcon :component="Check" aria-hidden="true" />
                  </template>
                  应用关系
                </NButton>
              </template>
              该关系会进入正式知识图谱，确认继续吗？
            </NPopconfirm>
            <NPopconfirm @positive-click="reject">
              <template #trigger>
                <NButton
                  type="error"
                  secondary
                  :loading="reviews.actionLoading"
                >
                  <template #icon>
                    <NIcon :component="X" aria-hidden="true" />
                  </template>
                  拒绝
                </NButton>
              </template>
              拒绝后建议进入终态，确定继续吗？
            </NPopconfirm>
          </footer>
        </template>
      </section>
    </div>
  </section>
</template>

<style scoped>
.relation-review {
  display: grid;
  gap: 16px;
}

.relation-summary {
  display: flex;
  align-items: center;
  gap: 28px;
  padding: 12px 0;
  border-top: 1px solid var(--cv-border);
  border-bottom: 1px solid var(--cv-border);
}

.relation-summary > div {
  display: flex;
  align-items: baseline;
  gap: 7px;
}

.relation-summary span {
  color: var(--cv-muted);
  font-size: 11px;
}

.relation-summary strong {
  font-size: 1.2rem;
  font-variant-numeric: tabular-nums;
}

.relation-summary .n-button {
  margin-left: auto;
}

.relation-filters {
  display: grid;
  grid-template-columns: 180px 220px;
  gap: 8px;
}

.relation-layout {
  display: grid;
  grid-template-columns: minmax(300px, 0.38fr) minmax(0, 1fr);
  gap: 22px;
}

.suggestion-list {
  display: grid;
  align-content: start;
  gap: 4px;
  border-right: 1px solid var(--cv-border);
  padding-right: 18px;
}

.suggestion-list button {
  display: grid;
  gap: 4px;
  width: 100%;
  min-width: 0;
  padding: 11px 9px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: inherit;
  text-align: left;
}

.suggestion-list button:hover,
.suggestion-list button.active {
  background: color-mix(in srgb, var(--cv-accent) 9%, transparent);
}

.suggestion-list button > span {
  overflow: hidden;
  color: var(--cv-muted);
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.suggestion-list .suggestion-title {
  color: var(--cv-text);
  font-size: 12px;
  font-weight: 650;
}

.suggestion-list .suggestion-meta {
  color: var(--cv-accent);
}

.suggestion-list button.active > span {
  color: color-mix(in srgb, var(--cv-text) 82%, var(--cv-muted));
}

.suggestion-detail {
  display: grid;
  min-width: 0;
  align-content: start;
  gap: 16px;
}

.detail-heading h2 {
  margin: 8px 0 0;
  font-size: 1.35rem;
  letter-spacing: 0;
}

.detail-heading p {
  margin: 3px 0 0;
  color: var(--cv-muted);
  font-size: 12px;
}

.detail-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.relation-documents {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
}

.relation-documents button {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 3px 8px;
  min-width: 0;
  padding: 11px;
  border: 1px solid var(--cv-border);
  border-radius: 6px;
  background: var(--cv-surface);
  color: inherit;
  text-align: left;
}

.relation-documents span,
.relation-documents small {
  color: var(--cv-muted);
  font-size: 10px;
}

.relation-documents strong {
  grid-column: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.relation-documents .n-icon {
  grid-column: 2;
  grid-row: 1 / span 3;
  align-self: center;
}

.reason-section,
.submitter-section,
.evidence-section {
  display: grid;
  gap: 8px;
  padding-top: 12px;
  border-top: 1px solid var(--cv-border);
}

.reason-section h3,
.submitter-section h3,
.evidence-section h3 {
  margin: 0;
  font-size: 13px;
}

.reason-section p {
  margin: 0;
  white-space: pre-wrap;
}

.submitter-section header,
.evidence-section header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.submitter-section header span,
.evidence-section header span {
  color: var(--cv-muted);
  font-size: 10px;
}

.submitter-section ul,
.evidence-section ul {
  display: grid;
  gap: 7px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.submitter-section li {
  color: var(--cv-muted);
  font-size: 12px;
}

.evidence-section li {
  display: grid;
  gap: 4px;
  padding-left: 9px;
  border-left: 2px solid var(--cv-border);
}

.evidence-section small {
  color: var(--cv-muted);
}

.evidence-section blockquote {
  margin: 0;
  padding: 8px 10px;
  background: color-mix(in srgb, var(--cv-accent) 5%, transparent);
  white-space: pre-wrap;
}

.detail-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.loading {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--cv-muted);
}

@media (max-width: 899px) {
  .relation-layout {
    grid-template-columns: 1fr;
  }

  .suggestion-list {
    border-right: 0;
    border-bottom: 1px solid var(--cv-border);
    padding: 0 0 14px;
  }
}

@media (max-width: 599px) {
  .relation-summary {
    flex-wrap: wrap;
    gap: 12px 20px;
  }

  .relation-summary .n-button {
    margin-left: 0;
  }

  .relation-filters,
  .relation-documents {
    grid-template-columns: 1fr;
  }
}
</style>
