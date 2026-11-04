<script setup lang="ts">
import { onBeforeUnmount, onMounted, watch } from "vue";
import {
  NAlert,
  NButton,
  NEmpty,
  NIcon,
  NPopconfirm,
  NSpin,
  NTag,
  useMessage,
} from "naive-ui";
import { X } from "@lucide/vue";
import { registerSensitiveStore } from "../../session/lifecycle";
import { useDocumentRelationsStore } from "../../stores/document-relations";
import type { RelationType } from "../../types/api";

const props = defineProps<{
  documentId: string;
}>();

const relations = useDocumentRelationsStore();
const message = useMessage();
let unregisterSensitiveStore: (() => void) | null = null;

const relationTypeLabels: Record<RelationType, string> = {
  related: "相关",
  example_of: "案例",
  implements: "实现",
  contrasts_with: "对比",
  depends_on: "依赖",
};

const directionLabels = {
  outgoing: "指向",
  incoming: "来自",
  symmetric: "相互",
} as const;

const load = (force = false): Promise<void> =>
  relations.load(props.documentId, force);

const retract = async (relationId: string): Promise<void> => {
  if (await relations.retract(relationId)) {
    message.success("已撤销正式关系。");
  } else {
    message.error(relations.error?.message ?? "撤销正式关系失败。");
  }
};

watch(
  () => props.documentId,
  () => void load(true),
);

onMounted(() => {
  unregisterSensitiveStore = registerSensitiveStore(relations);
  void load();
});

onBeforeUnmount(() => {
  unregisterSensitiveStore?.();
  unregisterSensitiveStore = null;
  relations.resetSensitive();
});
</script>

<template>
  <section class="confirmed-relations" aria-label="已确认关系">
    <header>
      <strong>已确认关系</strong>
      <span>{{ relations.items.length }} 条</span>
    </header>
    <NAlert v-if="relations.error" type="error" :show-icon="false">
      {{ relations.error.message }}
    </NAlert>
    <div v-if="relations.loading" class="relation-loading">
      <NSpin size="small" />
      <span>正在读取已确认关系…</span>
    </div>
    <div v-else-if="relations.items.length" class="relation-list">
      <article
        v-for="item in relations.items"
        :key="item.relationId"
        class="relation-item"
      >
        <div>
          <NTag size="tiny" :bordered="false">
            {{ relationTypeLabels[item.relationType] }}
          </NTag>
          <span>{{ directionLabels[item.direction] }}</span>
          <strong>{{ item.relatedDocument.title }}</strong>
        </div>
        <p>{{ item.relatedDocument.virtualPath }}</p>
        <NPopconfirm @positive-click="retract(item.relationId)">
          <template #trigger>
            <NButton
              quaternary
              circle
              size="tiny"
              aria-label="撤销正式关系"
              :loading="relations.actionLoading"
            >
              <template #icon>
                <NIcon :component="X" aria-hidden="true" />
              </template>
            </NButton>
          </template>
          撤销后该关系将从知识图谱移除，确定继续吗？
        </NPopconfirm>
      </article>
    </div>
    <NEmpty v-else description="暂无已确认关系" size="small" />
    <p v-if="relations.truncated" class="truncated">
      仅显示前 200 条已确认关系。
    </p>
  </section>
</template>

<style scoped>
.confirmed-relations {
  display: grid;
  gap: 8px;
  padding-top: 12px;
  border-top: 1px solid var(--cv-border);
}

.confirmed-relations > header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.confirmed-relations > header span {
  color: var(--cv-muted);
  font-size: 10px;
}

.relation-loading {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--cv-muted);
  font-size: 11px;
}

.relation-list {
  display: grid;
  gap: 4px;
}

.relation-item {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 2px 8px;
  padding: 7px 4px;
}

.relation-item > div {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 5px;
}

.relation-item strong {
  overflow: hidden;
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.relation-item > div > span {
  color: var(--cv-muted);
  font-size: 10px;
}

.relation-item p {
  grid-column: 1;
  overflow: hidden;
  margin: 0;
  color: var(--cv-muted);
  font-size: 10px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.relation-item .n-button {
  grid-column: 2;
  grid-row: 1 / span 2;
}

.truncated {
  margin: 0;
  color: var(--cv-warning);
  font-size: 10px;
}
</style>
