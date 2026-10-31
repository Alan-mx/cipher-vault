<script setup lang="ts">
import { onMounted, ref } from "vue";
import { useRouter } from "vue-router";
import { NAlert, NButton, NSpin } from "naive-ui";
import CatalogSidebar from "../components/navigation/CatalogSidebar.vue";
import DocumentList from "../components/document/DocumentList.vue";
import { useCatalogStore } from "../stores/catalog";
import CreateDocumentDialog from "../components/document/CreateDocumentDialog.vue";

const catalog = useCatalogStore();
const router = useRouter();
const createOpen = ref(false);

onMounted(async () => {
  if (!catalog.items.length) {
    await Promise.all([catalog.load(false), catalog.loadTags()]);
  }
});

const openCreated = async (documentId: string): Promise<void> => {
  await catalog.load(false);
  await router.push({ name: "document", params: { id: documentId } });
};
</script>

<template>
  <div class="documents-view">
    <CatalogSidebar />
    <section class="documents-main">
      <header class="documents-heading">
        <div>
          <p>本地知识库</p>
          <h1>全部文档</h1>
        </div>
        <NButton type="primary" @click="createOpen = true"> 新建文档 </NButton>
      </header>

      <NAlert
        v-if="catalog.error"
        type="error"
        :show-icon="false"
        closable
        @close="catalog.error = null"
      >
        {{ catalog.error.message }}
      </NAlert>

      <div
        v-if="catalog.loading && !catalog.items.length"
        class="loading-block"
      >
        <NSpin size="small" />
        <span>正在加载文档…</span>
      </div>
      <DocumentList v-else :items="catalog.items" />

      <div v-if="catalog.nextCursor" class="load-more">
        <NButton
          secondary
          :loading="catalog.loading"
          @click="catalog.load(true)"
        >
          加载更多
        </NButton>
      </div>
    </section>
    <CreateDocumentDialog v-model:show="createOpen" @created="openCreated" />
  </div>
</template>

<style scoped>
.documents-view {
  display: flex;
  min-height: calc(100vh - 56px);
}

.documents-main {
  width: min(100%, 960px);
  padding: 36px 40px 80px;
}

.documents-heading {
  display: flex;
  align-items: end;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 28px;
}

.documents-heading p {
  margin: 0 0 5px;
  color: var(--cv-muted);
  font-size: 12px;
}

.documents-heading h1 {
  margin: 0;
  font-size: 2rem;
  font-weight: 650;
  letter-spacing: 0;
}

.loading-block {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 48px 0;
  color: var(--cv-muted);
}

.load-more {
  display: flex;
  justify-content: center;
  padding-top: 24px;
}

@media (max-width: 899px) {
  .documents-view {
    display: block;
  }

  .documents-main {
    padding: 24px 20px 64px;
  }
}

@media (max-width: 560px) {
  .documents-heading {
    align-items: start;
    flex-direction: column;
  }

  .documents-heading h1 {
    font-size: 1.6rem;
  }
}
</style>
