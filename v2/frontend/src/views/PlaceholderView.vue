<script setup lang="ts">
import { computed } from "vue";
import { useRoute } from "vue-router";

const route = useRoute();
const title = computed(() =>
  typeof route.meta.title === "string" ? route.meta.title : "CipherVault",
);
</script>

<template>
  <main class="placeholder-page">
    <h1>{{ title }}</h1>
    <p>页面正在接入。</p>
  </main>
</template>

<style scoped>
.placeholder-page {
  padding: 32px;
}

.placeholder-page h1 {
  margin: 0 0 12px;
  font-size: 1.75rem;
}

.placeholder-page p {
  margin: 0;
  color: var(--cv-muted);
}
</style>
