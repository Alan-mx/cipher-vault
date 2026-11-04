<script setup lang="ts">
import { computed, ref } from "vue";
import { useRoute, useRouter } from "vue-router";
import { NButton, NDrawer, NDrawerContent, NIcon, NTooltip } from "naive-ui";
import {
  Activity,
  BookOpenText,
  ClipboardCheck,
  FileInput,
  LayoutGrid,
  LockKeyhole,
  Menu,
  Network,
  Settings,
  Trash2,
} from "@lucide/vue";
import { useSessionStore } from "../stores/session";
import IdleWarningDialog from "../components/lifecycle/IdleWarningDialog.vue";
import SearchBox from "../components/navigation/SearchBox.vue";
import SearchDialog from "../components/navigation/SearchDialog.vue";

const session = useSessionStore();
const route = useRoute();
const router = useRouter();
const drawerOpen = ref(false);
const searchOpen = ref(false);

const items = [
  { name: "documents", label: "知识库", icon: BookOpenText },
  { name: "knowledge-views", label: "知识视图", icon: LayoutGrid },
  { name: "review", label: "待审核", icon: ClipboardCheck },
  { name: "graph", label: "知识网络", icon: Network },
  { name: "trash", label: "回收站", icon: Trash2 },
  { name: "import", label: "导入", icon: FileInput },
  { name: "audit", label: "活动记录", icon: Activity },
  { name: "settings", label: "设置", icon: Settings },
] as const;

const title = computed(() =>
  typeof route.meta.title === "string" ? route.meta.title : "CipherVault",
);

const navigate = async (name: string): Promise<void> => {
  drawerOpen.value = false;
  await router.push({ name });
};

const openSearchResult = async (documentId: string): Promise<void> => {
  searchOpen.value = false;
  await router.push({
    name: "document",
    params: { id: documentId },
  });
};

const lock = async (): Promise<void> => {
  await session.lock();
  await router.replace({ name: "unlock" });
};
</script>

<template>
  <div class="workspace-shell">
    <aside class="workspace-rail" aria-label="主导航">
      <button class="rail-brand" type="button" aria-label="CipherVault 首页">
        CV
      </button>
      <nav class="rail-nav">
        <NTooltip
          v-for="item in items"
          :key="item.name"
          placement="right"
          trigger="hover"
        >
          <template #trigger>
            <button
              class="rail-link"
              :class="{
                'rail-link--active':
                  route.name === item.name ||
                  (item.name === 'documents' && route.name === 'document'),
              }"
              type="button"
              :aria-label="item.label"
              @click="navigate(item.name)"
            >
              <NIcon :component="item.icon" size="19" aria-hidden="true" />
            </button>
          </template>
          {{ item.label }}
        </NTooltip>
      </nav>
      <button
        class="rail-link rail-lock"
        type="button"
        aria-label="锁定 Vault"
        @click="lock"
      >
        <NIcon :component="LockKeyhole" size="19" aria-hidden="true" />
      </button>
    </aside>

    <div class="workspace-body">
      <header class="workspace-topbar">
        <div class="workspace-heading">
          <NButton
            class="mobile-menu"
            quaternary
            circle
            aria-label="打开导航"
            @click="drawerOpen = true"
          >
            <template #icon>
              <NIcon :component="Menu" aria-hidden="true" />
            </template>
          </NButton>
          <span>{{ title }}</span>
        </div>
        <div class="workspace-actions">
          <SearchBox @open="searchOpen = true" />
          <NButton quaternary size="small" @click="lock">
            <template #icon>
              <NIcon :component="LockKeyhole" aria-hidden="true" />
            </template>
            锁定
          </NButton>
        </div>
      </header>

      <main v-if="!session.revalidating" class="workspace-content">
        <slot />
      </main>
      <section v-else class="workspace-revalidating" aria-live="polite">
        <p>正在重新确认 Vault 状态…</p>
      </section>
    </div>

    <NDrawer v-model:show="drawerOpen" placement="left" :width="280">
      <NDrawerContent title="CipherVault" closable>
        <nav class="drawer-nav">
          <button
            v-for="item in items"
            :key="item.name"
            type="button"
            :class="{
              'drawer-link--active':
                route.name === item.name ||
                (item.name === 'documents' && route.name === 'document'),
            }"
            @click="navigate(item.name)"
          >
            <NIcon :component="item.icon" aria-hidden="true" />
            {{ item.label }}
          </button>
          <button type="button" @click="lock">
            <NIcon :component="LockKeyhole" aria-hidden="true" />
            锁定 Vault
          </button>
        </nav>
      </NDrawerContent>
    </NDrawer>

    <SearchDialog v-model:show="searchOpen" @open="openSearchResult" />
    <IdleWarningDialog />
  </div>
</template>

<style scoped>
.workspace-shell {
  display: flex;
  min-height: 100vh;
  background: var(--cv-bg);
}

.workspace-rail {
  position: sticky;
  top: 0;
  display: flex;
  width: 64px;
  height: 100vh;
  flex: 0 0 64px;
  flex-direction: column;
  align-items: center;
  padding: 12px 8px;
  border-right: 1px solid var(--cv-border);
  background: var(--cv-surface);
}

.rail-brand {
  display: grid;
  width: 40px;
  height: 40px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: transparent;
  color: var(--cv-accent);
  font-size: 11px;
  font-weight: 800;
  place-items: center;
}

.rail-nav {
  display: grid;
  gap: 4px;
  margin-top: 24px;
}

.rail-link {
  display: grid;
  width: 40px;
  height: 40px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--cv-muted);
  place-items: center;
}

.rail-link:hover,
.rail-link:focus-visible {
  background: color-mix(in srgb, var(--cv-accent) 10%, transparent);
  color: var(--cv-accent);
}

.rail-link--active {
  background: color-mix(in srgb, var(--cv-accent) 14%, transparent);
  color: var(--cv-accent);
}

.rail-lock {
  margin-top: auto;
}

.workspace-body {
  display: flex;
  min-width: 0;
  flex: 1;
  flex-direction: column;
}

.workspace-topbar {
  position: sticky;
  z-index: 10;
  top: 0;
  display: flex;
  height: 56px;
  flex: 0 0 56px;
  align-items: center;
  justify-content: space-between;
  padding: 0 20px;
  border-bottom: 1px solid var(--cv-border);
  background: color-mix(in srgb, var(--cv-surface) 94%, transparent);
  backdrop-filter: blur(14px);
}

.workspace-heading {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 14px;
  font-weight: 650;
}

.mobile-menu {
  display: none;
}

.workspace-content {
  min-width: 0;
  flex: 1;
}

.workspace-revalidating {
  display: grid;
  flex: 1;
  color: var(--cv-muted);
  place-items: center;
}

.drawer-nav {
  display: grid;
  gap: 4px;
}

.drawer-nav button {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 44px;
  padding: 0 12px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--cv-text);
  text-align: left;
}

.drawer-nav button:hover,
.drawer-nav button:focus-visible,
.drawer-link--active {
  background: color-mix(in srgb, var(--cv-accent) 12%, transparent);
  color: var(--cv-accent);
}

@media (min-width: 900px) {
  .mobile-menu {
    display: none;
  }
}

@media (max-width: 899px) {
  .workspace-rail {
    display: none;
  }

  .mobile-menu {
    display: inline-flex;
  }

  .workspace-topbar {
    padding: 0 12px;
  }
}
</style>
