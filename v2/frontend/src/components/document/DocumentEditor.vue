<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from "vue";
import { useRouter } from "vue-router";
import {
  NAlert,
  NButton,
  NIcon,
  NInput,
  NPopconfirm,
  NRadioButton,
  NRadioGroup,
  NTag,
  useMessage,
} from "naive-ui";
import { Trash2 } from "@lucide/vue";
import { useDocumentStore } from "../../stores/document";
import DocumentCodeEditor from "./DocumentCodeEditor.vue";
import MarkdownPreview from "./MarkdownPreview.vue";
import SaveConflictDialog from "./SaveConflictDialog.vue";

const documents = useDocumentStore();
const message = useMessage();
const router = useRouter();
const mode = ref<"edit" | "split" | "preview">(
  window.innerWidth < 900 ? "edit" : "split",
);

const save = async (): Promise<void> => {
  if (!documents.dirty || documents.conflict) {
    return;
  }
  try {
    const result = await documents.save();
    if (result) {
      message.success(`已保存为 Revision ${result.revision}`);
    }
  } catch {
    // The store exposes the actionable error or conflict dialog.
  }
};

const handleShortcut = (event: KeyboardEvent): void => {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "s") {
    event.preventDefault();
    void save();
  }
};

const remove = async (): Promise<void> => {
  try {
    await documents.deleteDocument();
    message.success("文档已移入回收站。");
    await router.push({ name: "documents" });
  } catch (caught) {
    message.error(caught instanceof Error ? caught.message : "删除文档失败。");
  }
};

onMounted(() => window.addEventListener("keydown", handleShortcut));
onBeforeUnmount(() => window.removeEventListener("keydown", handleShortcut));
</script>

<template>
  <section v-if="documents.metadata && documents.draft">
    <header class="document-header">
      <div class="document-fields">
        <NInput
          :value="documents.draft.title"
          aria-label="文档标题"
          placeholder="文档标题"
          @update:value="documents.updateDraft({ title: $event })"
        />
        <NInput
          :value="documents.draft.virtualPath"
          aria-label="虚拟路径"
          placeholder="虚拟路径"
          @update:value="documents.updateDraft({ virtualPath: $event })"
        />
      </div>
      <NButton
        type="primary"
        :loading="documents.saveState === 'saving'"
        :disabled="!documents.dirty || documents.saveState === 'saving'"
        @click="save"
      >
        保存
      </NButton>
      <NPopconfirm @positive-click="remove">
        <template #trigger>
          <NButton
            type="error"
            secondary
            :disabled="documents.saveState === 'saving'"
          >
            <template #icon>
              <NIcon :component="Trash2" aria-hidden="true" />
            </template>
            删除
          </NButton>
        </template>
        文档将移入回收站，可在 30 天后手动永久删除。
      </NPopconfirm>
    </header>

    <div class="document-tags">
      <NTag
        v-for="tag in documents.metadata.tags"
        :key="tag"
        size="small"
        :bordered="false"
      >
        {{ tag }}
      </NTag>
      <span v-if="documents.metadata.format === 'md'" class="tag-source">
        标签来自 Frontmatter
      </span>
    </div>

    <NAlert
      v-if="documents.error"
      class="save-error"
      type="error"
      :show-icon="false"
    >
      {{ documents.error.message }}
    </NAlert>
    <NAlert
      v-if="documents.refreshWarning"
      class="save-error"
      type="warning"
      :show-icon="false"
    >
      {{ documents.refreshWarning }}
    </NAlert>

    <div class="editor-toolbar">
      <NRadioGroup v-model:value="mode" size="small">
        <NRadioButton value="edit"> 编辑 </NRadioButton>
        <NRadioButton value="split"> 分栏 </NRadioButton>
        <NRadioButton value="preview"> 预览 </NRadioButton>
      </NRadioGroup>
    </div>

    <div class="editor-workbench" :class="`mode-${mode}`">
      <DocumentCodeEditor
        v-show="mode !== 'preview'"
        :model-value="documents.draft.content"
        :format="documents.metadata.format"
        @update:model-value="documents.updateDraft({ content: $event })"
      />
      <MarkdownPreview
        v-show="mode !== 'edit'"
        :markdown="documents.draft.content"
        :map="documents.map"
      />
    </div>
    <footer class="document-footer">
      R{{ documents.loadedRevision }} ·
      {{ documents.draft.content.length }} 字符 ·
      <span :class="{ dirty: documents.dirty }">
        {{ documents.dirty ? "未保存" : "已保存" }}
      </span>
      · Ctrl+S 保存
    </footer>
  </section>
  <SaveConflictDialog />
</template>

<style scoped>
.document-header {
  display: flex;
  align-items: start;
  justify-content: space-between;
  gap: 20px;
}

.document-fields {
  display: grid;
  width: min(100%, 680px);
  gap: 8px;
}

.document-tags {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin-top: 12px;
}

.tag-source {
  color: var(--cv-muted);
  font-size: 11px;
}

.save-error {
  margin-top: 16px;
}

.editor-toolbar {
  display: flex;
  justify-content: flex-end;
  margin-top: 24px;
}

.editor-workbench {
  display: grid;
  gap: 16px;
  margin-top: 10px;
}

.editor-workbench.mode-split {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.editor-workbench.mode-edit > .markdown-preview,
.editor-workbench.mode-preview > .document-code-editor {
  display: none !important;
}

.editor-workbench.mode-split > .document-code-editor,
.editor-workbench.mode-split > .markdown-preview {
  min-width: 0;
}

.document-footer {
  padding-top: 10px;
  color: var(--cv-muted);
  font-size: 12px;
}

.document-footer .dirty {
  color: var(--cv-warning);
}

@media (max-width: 899px) {
  .editor-workbench.mode-split {
    grid-template-columns: 1fr;
  }
}
</style>
