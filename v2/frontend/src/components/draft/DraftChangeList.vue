<script setup lang="ts">
import type { DraftFieldChange } from "../../types/knowledge-base";

defineProps<{
  changes: DraftFieldChange[];
  truncated: boolean;
}>();

const kindLabel = (kind: DraftFieldChange["kind"]): string =>
  ({
    new: "新建",
    added: "新增",
    modified: "修改",
    removed: "删除",
    baseline_unavailable: "无基线",
  })[kind];
</script>

<template>
  <section class="changes" aria-label="字段变化">
    <h3>字段变化</h3>
    <p v-if="changes.length === 0" class="muted">没有字段变化。</p>
    <ol v-else>
      <li v-for="change in changes" :key="change.fieldPath">
        <div class="change-header">
          <code>{{ change.fieldPath }}</code>
          <span :class="`kind-${change.kind}`">{{
            kindLabel(change.kind)
          }}</span>
        </div>
        <div class="change-values">
          <div>
            <span>修改前</span>
            <code>{{ change.before ?? "—" }}</code>
            <small v-if="change.beforeTruncated">已截断</small>
          </div>
          <div>
            <span>修改后</span>
            <code>{{ change.after ?? "—" }}</code>
            <small v-if="change.afterTruncated">已截断</small>
          </div>
        </div>
      </li>
    </ol>
    <p v-if="truncated" class="muted">字段变化过多，仅显示前 200 项。</p>
  </section>
</template>

<style scoped>
.changes {
  margin-top: 18px;
}
h3 {
  margin: 0 0 8px;
  font-size: 15px;
}
ol {
  display: grid;
  gap: 8px;
  margin: 0;
  padding: 0;
  list-style: none;
}
li {
  padding: 10px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-bg);
}
.change-header,
.change-values {
  display: flex;
  gap: 10px;
}
.change-header {
  align-items: center;
  justify-content: space-between;
}
.change-values {
  margin-top: 8px;
}
.change-values > div {
  display: grid;
  flex: 1;
  min-width: 0;
  gap: 3px;
}
.change-values span,
.change-values small,
.muted {
  color: var(--cv-muted);
  font-size: 12px;
}
code {
  overflow-wrap: anywhere;
  white-space: pre-wrap;
}
.kind-new,
.kind-added {
  color: #2e7d4f;
}
.kind-modified {
  color: #9a6700;
}
.kind-removed {
  color: #b93f36;
}
.kind-baseline_unavailable {
  color: #6b5ca5;
}
@media (max-width: 700px) {
  .change-values {
    flex-direction: column;
  }
}
</style>
