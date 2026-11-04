<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { NAlert, NButton, NForm, NFormItem, NInput, NModal } from "naive-ui";
import { useDocumentStore } from "../../stores/document";
import { describeApiError } from "../../utils/api-error";

const props = defineProps<{
  show: boolean;
}>();
const emit = defineEmits<{
  "update:show": [value: boolean];
  copied: [documentId: string];
}>();

const documents = useDocumentStore();
const title = ref("");
const virtualPath = ref("");
const submitting = ref(false);
const errorMessage = ref("");
const canSubmit = computed(
  () => title.value.trim().length > 0 && !submitting.value,
);

watch(
  () => props.show,
  (show) => {
    if (!show) {
      return;
    }
    const suffix = " 副本";
    const sourceTitle = documents.metadata?.title ?? "文档";
    title.value =
      Array.from(sourceTitle)
        .slice(0, 200 - Array.from(suffix).length)
        .join("") + suffix;
    virtualPath.value = "";
    errorMessage.value = "";
  },
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
    const result = await documents.copy({
      title: title.value.trim(),
      ...(virtualPath.value.trim()
        ? { virtualPath: virtualPath.value.trim() }
        : {}),
    });
    close();
    emit("copied", result.documentId);
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
      class="copy-dialog"
      role="dialog"
      aria-modal="true"
      aria-label="复制文档"
    >
      <header>
        <p>复制为新知识</p>
        <h2>生成独立文档身份</h2>
      </header>
      <NAlert v-if="errorMessage" type="error" :show-icon="false">
        {{ errorMessage }}
      </NAlert>
      <NForm class="copy-form" @submit.prevent="submit">
        <NFormItem label="标题">
          <NInput v-model:value="title" aria-label="复制文档标题" />
        </NFormItem>
        <NFormItem label="虚拟路径">
          <NInput
            v-model:value="virtualPath"
            placeholder="留空则自动生成唯一路径"
            aria-label="复制文档虚拟路径"
          />
        </NFormItem>
        <p class="copy-note">
          新文档使用新的稳定 ID 和 Revision 1，不复制历史版本或关系记录。
        </p>
        <div class="dialog-actions">
          <NButton @click="close">取消</NButton>
          <NButton
            type="primary"
            attr-type="submit"
            :loading="submitting"
            :disabled="!canSubmit"
          >
            复制
          </NButton>
        </div>
      </NForm>
    </section>
  </NModal>
</template>

<style scoped>
.copy-dialog {
  width: min(94vw, 620px);
  padding: 24px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}

.copy-dialog header p {
  margin: 0 0 5px;
  color: var(--cv-muted);
  font-size: 12px;
}

.copy-dialog header h2 {
  margin: 0;
  font-size: 1.5rem;
}

.copy-form {
  display: grid;
  gap: 6px;
  margin-top: 20px;
}

.copy-note {
  margin: 4px 0 12px;
  color: var(--cv-muted);
  font-size: 12px;
  line-height: 1.55;
}

.dialog-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
