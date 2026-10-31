<script setup lang="ts">
import { onMounted, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import {
  NAlert,
  NButton,
  NDrawer,
  NDrawerContent,
  NIcon,
  NSpin,
} from "naive-ui";
import { PanelRight } from "@lucide/vue";
import DocumentEditor from "../components/document/DocumentEditor.vue";
import DocumentInspector from "../components/document/DocumentInspector.vue";
import { useDocumentStore } from "../stores/document";
import { isGuid } from "../utils/guid";

const route = useRoute();
const router = useRouter();
const documents = useDocumentStore();
const inspectorOpen = ref(false);

const load = async (): Promise<void> => {
  const id = typeof route.params.id === "string" ? route.params.id : "";
  if (!isGuid(id)) {
    await router.replace({ name: "not-found" });
    return;
  }
  await documents.open(id);
  if (!documents.error) {
    await documents.loadInspector();
  }
};

onMounted(load);
watch(() => route.params.id, load);
</script>

<template>
  <section class="document-view">
    <div v-if="documents.loading" class="document-loading">
      <NSpin size="small" />
      <span>正在读取文档…</span>
    </div>
    <NAlert v-else-if="documents.error" type="error" :show-icon="false">
      {{ documents.error.message }}
    </NAlert>
    <div
      v-else-if="documents.metadata && documents.draft"
      class="document-grid"
    >
      <div class="document-main">
        <NButton
          class="inspector-toggle"
          quaternary
          size="small"
          @click="inspectorOpen = true"
        >
          <template #icon>
            <NIcon :component="PanelRight" aria-hidden="true" />
          </template>
          文档信息
        </NButton>
        <DocumentEditor />
      </div>
      <DocumentInspector class="desktop-inspector" />
    </div>

    <NDrawer v-model:show="inspectorOpen" placement="right" :width="360">
      <NDrawerContent title="文档信息" closable>
        <DocumentInspector />
      </NDrawerContent>
    </NDrawer>
  </section>
</template>

<style scoped>
.document-view {
  width: 100%;
  padding: 32px 40px 80px;
}

.document-loading {
  display: flex;
  align-items: center;
  gap: 12px;
  color: var(--cv-muted);
}

.document-grid {
  display: grid;
  width: min(100%, 1500px);
  grid-template-columns: minmax(0, 1fr) 340px;
  gap: 24px;
  margin: 0 auto;
}

.document-main {
  position: relative;
  min-width: 0;
}

.inspector-toggle {
  display: none;
  margin-bottom: 8px;
}

@media (max-width: 899px) {
  .document-view {
    padding: 24px 20px 64px;
  }
}

@media (max-width: 1099px) {
  .document-grid {
    display: block;
  }

  .desktop-inspector {
    display: none;
  }

  .inspector-toggle {
    display: inline-flex;
  }
}
</style>
