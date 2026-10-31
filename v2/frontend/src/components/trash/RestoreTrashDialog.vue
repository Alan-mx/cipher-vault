<script setup lang="ts">
import { NAlert, NButton, NInput, NModal } from "naive-ui";
import type { TrashItem } from "../../types/api";

defineProps<{
  show: boolean;
  item: TrashItem | null;
  replacementPath: string;
  loading: boolean;
  errorMessage: string;
}>();

const emit = defineEmits<{
  "update:show": [value: boolean];
  "update:replacementPath": [value: string];
  confirm: [];
}>();
</script>

<template>
  <NModal :show="show" @update:show="emit('update:show', $event)">
    <section
      class="restore-dialog"
      role="dialog"
      aria-modal="true"
      aria-label="恢复文档"
    >
      <h2>恢复文档</h2>
      <p>“{{ item?.title }}” 的原路径已被占用。请输入新的虚拟路径后再恢复。</p>
      <NAlert v-if="errorMessage" type="error" :show-icon="false">
        {{ errorMessage }}
      </NAlert>
      <NInput
        :value="replacementPath"
        placeholder="例如 Restored/Note.md"
        @update:value="emit('update:replacementPath', $event)"
      />
      <div class="restore-actions">
        <NButton @click="emit('update:show', false)"> 取消 </NButton>
        <NButton
          type="primary"
          :loading="loading"
          :disabled="!replacementPath.trim()"
          @click="emit('confirm')"
        >
          恢复
        </NButton>
      </div>
    </section>
  </NModal>
</template>

<style scoped>
.restore-dialog {
  display: grid;
  width: min(92vw, 520px);
  gap: 16px;
  padding: 24px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}

.restore-dialog h2,
.restore-dialog p {
  margin: 0;
}

.restore-dialog p {
  color: var(--cv-muted);
  line-height: 1.6;
}

.restore-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
