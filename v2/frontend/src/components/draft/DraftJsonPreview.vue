<script setup lang="ts">
defineProps<{
  json: string;
  truncated: boolean;
}>();
</script>

<template>
  <details class="json-preview">
    <summary>Raw JSON</summary>
    <p v-if="truncated" class="muted">仅显示 payload 前 16,384 个字符。</p>
    <pre tabindex="0">{{ json }}</pre>
  </details>
</template>

<style scoped>
.json-preview {
  margin-top: 18px;
}
summary {
  cursor: pointer;
  color: var(--cv-muted);
}
.muted {
  color: var(--cv-muted);
  font-size: 12px;
}
pre {
  max-height: 360px;
  overflow: auto;
  padding: 12px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-bg);
  white-space: pre-wrap;
}
</style>
