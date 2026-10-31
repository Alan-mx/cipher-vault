<script setup lang="ts">
import { NIcon } from "naive-ui";
import { Search } from "@lucide/vue";

defineEmits<{
  open: [];
}>();
</script>

<template>
  <button
    class="search-box"
    type="button"
    aria-label="搜索文档"
    @click="$emit('open')"
  >
    <NIcon :component="Search" size="15" aria-hidden="true" />
    <span>搜索文档</span>
  </button>
</template>

<style scoped>
.search-box {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  min-width: 180px;
  height: 34px;
  padding: 0 12px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: color-mix(in srgb, var(--cv-surface) 78%, transparent);
  color: var(--cv-muted);
  cursor: pointer;
}

.search-box:hover,
.search-box:focus-visible {
  border-color: var(--cv-accent);
  color: var(--cv-accent);
}

@media (max-width: 699px) {
  .search-box {
    min-width: 40px;
  }

  .search-box span {
    display: none;
  }
}
</style>
