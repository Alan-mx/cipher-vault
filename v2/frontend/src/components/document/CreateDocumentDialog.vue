<script setup lang="ts">
import { computed, ref } from "vue";
import {
  NAlert,
  NButton,
  NForm,
  NFormItem,
  NModal,
  NRadioButton,
  NRadioGroup,
  NTag,
} from "naive-ui";
import { useDocumentStore } from "../../stores/document";
import type { VaultFormat } from "../../types/domain";
import { describeApiError } from "../../utils/api-error";

defineProps<{
  show: boolean;
}>();
const emit = defineEmits<{
  "update:show": [value: boolean];
  created: [documentId: string];
}>();

const documents = useDocumentStore();
const title = ref("");
const format = ref<VaultFormat>("md");
const pathMode = ref<"folder" | "path">("folder");
const folder = ref("");
const virtualPath = ref("");
const content = ref("");
const tags = ref("");
const submitting = ref(false);
const errorMessage = ref("");
const formatOptions = [
  { label: "Markdown", value: "md" },
  { label: "纯文本", value: "txt" },
] satisfies Array<{ label: string; value: VaultFormat }>;
const canSubmit = computed(
  () =>
    title.value.trim().length > 0 &&
    content.value.length > 0 &&
    !submitting.value,
);

const close = (): void => {
  emit("update:show", false);
};

const submit = async (): Promise<void> => {
  if (!canSubmit.value) {
    return;
  }
  errorMessage.value = "";
  submitting.value = true;
  try {
    const result = await documents.create({
      title: title.value.trim(),
      format: format.value,
      content: content.value,
      ...(pathMode.value === "folder" && folder.value.trim()
        ? { folder: folder.value.trim() }
        : {}),
      ...(pathMode.value === "path" && virtualPath.value.trim()
        ? { virtualPath: virtualPath.value.trim() }
        : {}),
      ...(format.value === "txt" && tags.value.trim()
        ? {
            tags: tags.value
              .split(",")
              .map((tag) => tag.trim())
              .filter((tag) => tag.length > 0),
          }
        : {}),
    });
    title.value = "";
    content.value = "";
    folder.value = "";
    virtualPath.value = "";
    tags.value = "";
    close();
    emit("created", result.documentId);
  } catch (caught) {
    errorMessage.value = describeApiError(caught);
  } finally {
    submitting.value = false;
  }
};
</script>

<template>
  <NModal :show="show" @update:show="emit('update:show', $event)">
    <section
      class="create-dialog"
      role="dialog"
      aria-modal="true"
      aria-label="新建文档"
    >
      <header>
        <p>新建文档</p>
        <h2>写入本地知识库</h2>
      </header>
      <NAlert v-if="errorMessage" type="error" :show-icon="false">
        {{ errorMessage }}
      </NAlert>
      <NForm class="create-form" @submit.prevent="submit">
        <NFormItem label="标题">
          <input
            v-model="title"
            class="dialog-input"
            placeholder="文档标题"
            aria-label="文档标题"
          />
        </NFormItem>
        <NFormItem label="格式">
          <select v-model="format" class="dialog-input" aria-label="文档格式">
            <option
              v-for="option in formatOptions"
              :key="option.value"
              :value="option.value"
            >
              {{ option.label }}
            </option>
          </select>
        </NFormItem>
        <NFormItem label="路径方式">
          <NRadioGroup v-model:value="pathMode">
            <NRadioButton value="folder"> 文件夹 </NRadioButton>
            <NRadioButton value="path"> 虚拟路径 </NRadioButton>
          </NRadioGroup>
        </NFormItem>
        <NFormItem v-if="pathMode === 'folder'" label="文件夹">
          <input
            v-model="folder"
            class="dialog-input"
            placeholder="例如 Notes/Projects"
            aria-label="文件夹"
          />
        </NFormItem>
        <NFormItem v-else label="虚拟路径">
          <input
            v-model="virtualPath"
            class="dialog-input"
            placeholder="例如 Notes/Project.md"
            aria-label="虚拟路径"
          />
        </NFormItem>
        <NFormItem v-if="format === 'txt'" label="标签">
          <input
            v-model="tags"
            class="dialog-input"
            placeholder="用逗号分隔标签"
            aria-label="文档标签"
          />
        </NFormItem>
        <NFormItem label="正文">
          <textarea
            v-model="content"
            class="dialog-textarea"
            placeholder="输入 Markdown 或 TXT 内容"
            aria-label="文档正文"
          />
        </NFormItem>
        <div class="dialog-actions">
          <NButton @click="close"> 取消 </NButton>
          <NButton
            type="primary"
            attr-type="submit"
            :loading="submitting"
            :disabled="!canSubmit"
          >
            创建
          </NButton>
        </div>
      </NForm>
      <NTag v-if="format === 'md'" class="frontmatter-hint" :bordered="false">
        Markdown 标签由 Frontmatter 自动解析。
      </NTag>
    </section>
  </NModal>
</template>

<style scoped>
.create-dialog {
  width: min(94vw, 720px);
  max-height: 90vh;
  padding: 24px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  overflow: auto;
}

.create-dialog header p {
  margin: 0 0 5px;
  color: var(--cv-muted);
  font-size: 12px;
}

.create-dialog header h2 {
  margin: 0;
  font-size: 1.5rem;
}

.create-form {
  display: grid;
  gap: 6px;
  margin-top: 20px;
}

.dialog-input,
.dialog-textarea {
  width: 100%;
  padding: 0 12px;
  border: 1px solid var(--cv-border);
  border-radius: 4px;
  background: var(--cv-surface);
  color: var(--cv-text);
  outline: none;
}

.dialog-input {
  height: 34px;
}

.dialog-textarea {
  min-height: 190px;
  padding-top: 10px;
  padding-bottom: 10px;
  line-height: 1.6;
  resize: vertical;
}

.dialog-input:focus,
.dialog-textarea:focus {
  border-color: var(--cv-accent);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--cv-accent) 20%, transparent);
}

.dialog-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 12px;
}

.frontmatter-hint {
  margin-top: 12px;
}
</style>
