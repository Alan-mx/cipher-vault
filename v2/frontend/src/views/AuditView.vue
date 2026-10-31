<script setup lang="ts">
import { onMounted, ref } from "vue";
import {
  NAlert,
  NButton,
  NDatePicker,
  NEmpty,
  NInput,
  NSelect,
  NSpin,
} from "naive-ui";
import { useAuditStore } from "../stores/audit";
import type { ActorKind } from "../types/domain";
import { formatAuditDetails } from "../utils/audit";

const audit = useAuditStore();
const after = ref<number | null>(null);
const before = ref<number | null>(null);
const actorOptions = [
  { label: "用户界面", value: "user_ui" },
  { label: "MCP", value: "mcp" },
  { label: "系统", value: "system" },
] satisfies Array<{ label: string; value: ActorKind }>;

onMounted(() => {
  if (!audit.items.length) {
    void audit.load(false);
  }
});

const updateAfter = (value: number | null): void => {
  after.value = value;
  audit.setFilters({
    occurredAfter: value === null ? null : new Date(value).toISOString(),
  });
};

const updateBefore = (value: number | null): void => {
  before.value = value;
  audit.setFilters({
    occurredBefore: value === null ? null : new Date(value).toISOString(),
  });
};
</script>

<template>
  <section class="audit-view">
    <header class="page-heading">
      <p>审计</p>
      <h1>活动记录</h1>
    </header>

    <div class="audit-filters">
      <NSelect
        :input-props="{ 'aria-label': '操作者过滤' }"
        :value="audit.filters.actor"
        :options="actorOptions"
        clearable
        placeholder="操作者"
        @update:value="audit.setFilters({ actor: $event as ActorKind | null })"
      />
      <NInput
        :value="audit.filters.action"
        placeholder="动作，例如 create"
        @update:value="audit.setFilters({ action: $event })"
      />
      <NInput
        :value="audit.filters.documentId"
        placeholder="Document ID"
        @update:value="audit.setFilters({ documentId: $event })"
      />
      <NDatePicker
        :input-props="{ 'aria-label': '开始时间' }"
        :value="after"
        type="datetime"
        clearable
        placeholder="开始时间"
        @update:value="updateAfter"
      />
      <NDatePicker
        :input-props="{ 'aria-label': '结束时间' }"
        :value="before"
        type="datetime"
        clearable
        placeholder="结束时间"
        @update:value="updateBefore"
      />
      <NButton
        type="primary"
        :loading="audit.loading"
        @click="audit.load(false)"
      >
        查询
      </NButton>
    </div>

    <NAlert v-if="audit.error" type="error" :show-icon="false">
      {{ audit.error.message }}
    </NAlert>

    <div v-if="audit.loading && !audit.items.length" class="loading">
      <NSpin size="small" />
      <span>正在读取活动记录…</span>
    </div>
    <NEmpty v-else-if="!audit.items.length" description="没有匹配的活动记录" />
    <div v-else class="audit-list">
      <article
        v-for="item in audit.items"
        :key="item.eventId"
        class="audit-row"
      >
        <div class="audit-title">
          <strong>{{ item.action }}</strong>
          <span>{{ item.actor }}</span>
          <span>{{ item.result }}</span>
        </div>
        <p>
          {{ new Date(item.occurredAt).toLocaleString("zh-CN") }}
          <template v-if="item.documentId"> · {{ item.documentId }} </template>
          <template v-if="item.revision"> · R{{ item.revision }} </template>
        </p>
        <pre>{{ formatAuditDetails(item.detailsJson) }}</pre>
      </article>
    </div>

    <NButton
      v-if="audit.nextCursor"
      class="load-more"
      secondary
      :loading="audit.loading"
      @click="audit.load(true)"
    >
      加载更多
    </NButton>
  </section>
</template>

<style scoped>
.audit-view {
  width: min(100%, 1120px);
  padding: 32px 40px 80px;
}

.page-heading p {
  margin: 0 0 5px;
  color: var(--cv-muted);
  font-size: 12px;
}

.page-heading h1 {
  margin: 0 0 24px;
  font-size: 2rem;
  letter-spacing: 0;
}

.audit-filters {
  display: grid;
  grid-template-columns:
    130px minmax(140px, 1fr) minmax(260px, 1.4fr)
    180px 180px auto;
  gap: 8px;
  margin-bottom: 16px;
}

.loading {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 36px 0;
  color: var(--cv-muted);
}

.audit-list {
  border-top: 1px solid var(--cv-border);
}

.audit-row {
  padding: 12px 0;
  border-bottom: 1px solid var(--cv-border);
}

.audit-title {
  display: flex;
  align-items: center;
  gap: 8px;
}

.audit-title strong {
  font-size: 13px;
}

.audit-title span {
  padding: 1px 6px;
  border: 1px solid var(--cv-border);
  border-radius: 4px;
  color: var(--cv-muted);
  font-size: 10px;
}

.audit-row p {
  margin: 5px 0;
  color: var(--cv-muted);
  font-size: 11px;
}

.audit-row pre {
  max-height: 180px;
  margin: 8px 0 0;
  padding: 8px;
  border-radius: 6px;
  background: var(--cv-bg);
  font-family: "Cascadia Code", Consolas, monospace;
  font-size: 10px;
  overflow: auto;
  white-space: pre-wrap;
}

.load-more {
  display: flex;
  margin: 20px auto 0;
}

@media (max-width: 1099px) {
  .audit-filters {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 699px) {
  .audit-view {
    padding: 24px 20px 64px;
  }

  .audit-filters {
    grid-template-columns: 1fr;
  }
}
</style>
