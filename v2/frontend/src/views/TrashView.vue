<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import {
  NAlert,
  NButton,
  NEmpty,
  NPopconfirm,
  NSpin,
  useMessage,
} from "naive-ui";
import { useTrashStore } from "../stores/trash";
import type { TrashItem } from "../types/api";
import { ApiClientError } from "../types/domain";
import { isPurgeEligible } from "../utils/trash";
import RestoreTrashDialog from "../components/trash/RestoreTrashDialog.vue";

const trash = useTrashStore();
const message = useMessage();
const restoreOpen = ref(false);
const restoreItem = ref<TrashItem | null>(null);
const replacementPath = ref("");
const restoreError = ref("");
const now = ref(Date.now());

const items = computed(() =>
  trash.items.map((item) => ({
    ...item,
    eligible: isPurgeEligible(item.purgeEligibleAt, now.value),
  })),
);

onMounted(() => trash.load(false));

const requestRestore = async (item: TrashItem): Promise<void> => {
  restoreError.value = "";
  const restored = await trash.restore(item);
  if (restored) {
    message.success("文档已恢复。");
    return;
  }
  if (
    trash.error instanceof ApiClientError &&
    trash.error.code === "document_path_conflict"
  ) {
    restoreItem.value = item;
    replacementPath.value = item.virtualPath;
    restoreError.value = "";
    restoreOpen.value = true;
  } else {
    message.error(trash.error?.message ?? "恢复失败。");
  }
};

const confirmRestore = async (): Promise<void> => {
  if (!restoreItem.value) {
    return;
  }
  const restored = await trash.restore(
    restoreItem.value,
    replacementPath.value.trim(),
  );
  if (restored) {
    restoreOpen.value = false;
    restoreItem.value = null;
    message.success("文档已以新路径恢复。");
  } else {
    restoreError.value = trash.error?.message ?? "恢复失败。";
  }
};

const purge = async (item: TrashItem): Promise<void> => {
  if (await trash.purge(item)) {
    message.success("文档已永久删除。");
  } else {
    message.error(trash.error?.message ?? "永久删除失败。");
  }
};
</script>

<template>
  <section class="trash-view">
    <header class="page-heading">
      <div>
        <p>回收站</p>
        <h1>已删除文档</h1>
      </div>
    </header>

    <NAlert v-if="trash.error && !restoreOpen" type="error" :show-icon="false">
      {{ trash.error.message }}
    </NAlert>

    <div v-if="trash.loading && !trash.items.length" class="loading">
      <NSpin size="small" />
      <span>正在读取回收站…</span>
    </div>
    <NEmpty v-else-if="!items.length" description="回收站为空" />
    <div v-else class="trash-list">
      <article v-for="item in items" :key="item.documentId" class="trash-row">
        <div>
          <strong>{{ item.title }}</strong>
          <p>{{ item.virtualPath }}</p>
          <span>
            删除于 {{ new Date(item.deletedAt).toLocaleString("zh-CN") }}
          </span>
        </div>
        <div class="trash-meta">
          <span>R{{ item.revision }}</span>
          <span>
            可永久删除：
            {{ new Date(item.purgeEligibleAt).toLocaleString("zh-CN") }}
          </span>
        </div>
        <div class="trash-actions">
          <NButton
            size="small"
            :loading="trash.actionLoading === item.documentId"
            @click="requestRestore(item)"
          >
            恢复
          </NButton>
          <NPopconfirm :disabled="!item.eligible" @positive-click="purge(item)">
            <template #trigger>
              <NButton
                size="small"
                type="error"
                secondary
                :disabled="!item.eligible"
                :loading="trash.actionLoading === item.documentId"
              >
                永久删除
              </NButton>
            </template>
            永久删除后无法恢复，确定继续吗？
          </NPopconfirm>
        </div>
      </article>
    </div>

    <NButton
      v-if="trash.nextCursor"
      class="load-more"
      secondary
      :loading="trash.loading"
      @click="trash.load(true)"
    >
      加载更多
    </NButton>

    <RestoreTrashDialog
      v-model:show="restoreOpen"
      v-model:replacement-path="replacementPath"
      :item="restoreItem"
      :loading="trash.actionLoading !== null"
      :error-message="restoreError"
      @confirm="confirmRestore"
    />
  </section>
</template>

<style scoped>
.trash-view {
  width: min(100%, 1080px);
  padding: 32px 40px 80px;
}

.page-heading p {
  margin: 0 0 5px;
  color: var(--cv-muted);
  font-size: 12px;
}

.page-heading h1 {
  margin: 0 0 28px;
  font-size: 2rem;
  letter-spacing: 0;
}

.loading {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 48px 0;
  color: var(--cv-muted);
}

.trash-list {
  border-top: 1px solid var(--cv-border);
}

.trash-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto auto;
  align-items: center;
  gap: 20px;
  padding: 16px 0;
  border-bottom: 1px solid var(--cv-border);
}

.trash-row strong {
  font-size: 14px;
}

.trash-row p,
.trash-row span {
  margin: 4px 0 0;
  color: var(--cv-muted);
  font-size: 11px;
}

.trash-meta {
  display: grid;
  justify-items: end;
  gap: 4px;
}

.trash-actions {
  display: flex;
  gap: 6px;
}

.load-more {
  display: flex;
  margin: 24px auto 0;
}

@media (max-width: 899px) {
  .trash-view {
    padding: 24px 20px 64px;
  }
}

@media (max-width: 699px) {
  .trash-row {
    grid-template-columns: 1fr;
    gap: 10px;
  }

  .trash-meta {
    justify-items: start;
  }
}
</style>
