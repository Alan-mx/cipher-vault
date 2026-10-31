<script setup lang="ts">
import {
  NAlert,
  NButton,
  NCheckbox,
  NEmpty,
  NPopconfirm,
  NSpin,
  useMessage,
} from "naive-ui";
import { useDocumentStore } from "../../stores/document";

const documents = useDocumentStore();
const message = useMessage();

const restore = async (revisionId: string): Promise<void> => {
  try {
    await documents.restoreRevision(revisionId);
    message.success("历史版本已恢复为新的当前版本。");
  } catch {
    message.error("恢复失败，请检查当前 Revision 或重试。");
  }
};
</script>

<template>
  <div class="revision-panel">
    <div v-if="documents.revisions.length" class="revision-list">
      <div
        v-for="revision in documents.revisions"
        :key="revision.revisionId"
        class="revision-row"
      >
        <NCheckbox
          :checked="documents.selectedRevisionIds.includes(revision.revisionId)"
          :aria-label="`选择 Revision ${revision.revision} 进行对比`"
          @update:checked="
            documents.toggleRevisionSelection(revision.revisionId)
          "
        />
        <div>
          <strong>R{{ revision.revision }}</strong>
          <p>{{ revision.virtualPath }}</p>
          <span>{{
            new Date(revision.createdAt).toLocaleString("zh-CN")
          }}</span>
        </div>
        <NPopconfirm @positive-click="restore(revision.revisionId)">
          <template #trigger>
            <NButton size="tiny" secondary> 恢复 </NButton>
          </template>
          将创建新的当前 Revision，历史记录不会被改写。
        </NPopconfirm>
      </div>
    </div>
    <NEmpty v-else description="没有保留的历史版本" size="small" />

    <NButton
      v-if="documents.revisionCursor"
      block
      size="tiny"
      secondary
      @click="documents.loadRevisions(true)"
    >
      加载更多
    </NButton>

    <div class="compare-actions">
      <span>选择两个版本进行对比</span>
      <NButton
        size="small"
        :disabled="documents.selectedRevisionIds.length !== 2"
        :loading="documents.revisionCompareLoading"
        @click="documents.compareSelectedRevisions()"
      >
        对比
      </NButton>
    </div>

    <NSpin v-if="documents.revisionCompareLoading" size="small" />
    <NAlert
      v-if="documents.revisionCompareError"
      type="error"
      :show-icon="false"
    >
      {{ documents.revisionCompareError.message }}
    </NAlert>
    <pre v-if="documents.revisionDiff" class="revision-diff">{{
      documents.revisionDiff
    }}</pre>
  </div>
</template>

<style scoped>
.revision-list {
  display: grid;
  gap: 4px;
}

.revision-row {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto;
  align-items: center;
  gap: 8px;
  padding: 8px;
  border-radius: 6px;
}

.revision-row:hover {
  background: color-mix(in srgb, var(--cv-accent) 6%, transparent);
}

.revision-row strong {
  font-size: 12px;
}

.revision-row p,
.revision-row span {
  overflow: hidden;
  margin: 2px 0 0;
  color: var(--cv-muted);
  font-size: 10px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.compare-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  margin-top: 14px;
  color: var(--cv-muted);
  font-size: 11px;
}

.revision-diff {
  max-height: 420px;
  margin: 12px 0 0;
  padding: 10px;
  border: 1px solid var(--cv-border);
  border-radius: 6px;
  background: var(--cv-bg);
  font-family: "Cascadia Code", Consolas, monospace;
  font-size: 10px;
  line-height: 1.55;
  overflow: auto;
  white-space: pre-wrap;
}
</style>
