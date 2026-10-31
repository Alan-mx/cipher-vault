<script setup lang="ts">
import { NAlert, NButton, NModal } from "naive-ui";
import { useDocumentStore } from "../../stores/document";

const documents = useDocumentStore();

const loadLatest = async (): Promise<void> => {
  await documents.reloadLatest();
};

const compareLatest = async (): Promise<void> => {
  await documents.loadLatestConflict();
};

const adoptLatest = (): void => {
  documents.adoptLatestRevision();
};
</script>

<template>
  <NModal :show="documents.conflict !== null" :mask-closable="false">
    <section
      class="conflict-dialog"
      role="alertdialog"
      aria-modal="true"
      aria-labelledby="conflict-title"
    >
      <p class="conflict-kicker">保存冲突</p>
      <h2 id="conflict-title">
        文档已被更新到 Revision
        {{ documents.conflict?.currentRevision ?? "?" }}
      </h2>
      <NAlert type="warning" :show-icon="false">
        本地草稿仍保留在内存中，没有被覆盖。请先查看最新内容，再决定如何继续。
      </NAlert>

      <div
        v-if="
          documents.conflict?.mode === 'compare' && documents.conflict.latest
        "
        class="compare-grid"
      >
        <label>
          <span>本地草稿</span>
          <textarea :value="documents.draft?.content" readonly />
        </label>
        <label>
          <span>服务器最新内容</span>
          <textarea :value="documents.conflict.latest.content" readonly />
        </label>
      </div>

      <div class="conflict-actions">
        <NButton @click="documents.discardLocalConflict()">
          放弃本地修改
        </NButton>
        <NButton @click="loadLatest"> 加载最新并丢弃草稿 </NButton>
        <NButton
          v-if="documents.conflict?.mode === 'notice'"
          secondary
          @click="compareLatest"
        >
          对比本地与最新
        </NButton>
        <NButton v-else type="primary" @click="adoptLatest">
          以最新 Revision 为基线继续编辑
        </NButton>
      </div>
    </section>
  </NModal>
</template>

<style scoped>
.conflict-dialog {
  width: min(94vw, 960px);
  max-height: 90vh;
  padding: 24px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  overflow: auto;
}

.conflict-kicker {
  margin: 0 0 6px;
  color: var(--cv-warning);
  font-size: 12px;
  font-weight: 700;
}

.conflict-dialog h2 {
  margin: 0 0 16px;
  font-size: 1.35rem;
}

.compare-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
  margin-top: 16px;
}

.compare-grid label {
  display: grid;
  gap: 6px;
  color: var(--cv-muted);
  font-size: 12px;
}

.compare-grid textarea {
  min-height: 300px;
  padding: 12px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-bg);
  color: var(--cv-text);
  font-family: "Cascadia Code", Consolas, monospace;
  font-size: 12px;
  line-height: 1.55;
  resize: vertical;
}

.conflict-actions {
  display: flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 20px;
}

@media (max-width: 699px) {
  .compare-grid {
    grid-template-columns: 1fr;
  }
}
</style>
