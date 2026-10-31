<script setup lang="ts">
import { NTag } from "naive-ui";
import type { ImportPreviewItem } from "../../types/api";

defineProps<{
  items: ImportPreviewItem[];
}>();

const statusType = (
  state: ImportPreviewItem["state"],
): "success" | "warning" | "error" =>
  state === "ready"
    ? "success"
    : state === "skipped_conflict"
      ? "warning"
      : "error";
</script>

<template>
  <div class="import-table">
    <div v-for="item in items" :key="item.relativePath" class="import-row">
      <div>
        <strong>{{ item.relativePath }}</strong>
        <p>{{ item.virtualPath }}</p>
      </div>
      <NTag size="small" :type="statusType(item.state)" :bordered="false">
        {{ item.state }}
      </NTag>
      <span>{{ item.format.toUpperCase() }}</span>
      <span>{{ Math.ceil(item.sizeBytes / 1024) }} KiB</span>
    </div>
  </div>
</template>

<style scoped>
.import-table {
  border-top: 1px solid var(--cv-border);
}

.import-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto 60px 80px;
  align-items: center;
  gap: 12px;
  padding: 10px 0;
  border-bottom: 1px solid var(--cv-border);
}

.import-row strong {
  font-size: 12px;
}

.import-row p,
.import-row span {
  margin: 3px 0 0;
  color: var(--cv-muted);
  font-size: 11px;
}

@media (max-width: 699px) {
  .import-row {
    grid-template-columns: minmax(0, 1fr) auto;
  }
}
</style>
