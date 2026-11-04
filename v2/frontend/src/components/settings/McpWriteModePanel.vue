<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from "vue";
import {
  NAlert,
  NButton,
  NButtonGroup,
  NIcon,
  NPopconfirm,
  NSpin,
  useMessage,
} from "naive-ui";
import { ShieldAlert, ShieldCheck } from "@lucide/vue";
import { useWriteModeStore } from "../../stores/write-mode";
import { registerSensitiveStore } from "../../session/lifecycle";
import type { McpWriteMode } from "../../types/api";

const writeMode = useWriteModeStore();
const message = useMessage();
let unregisterSensitiveStore: (() => void) | null = null;

const mode = computed(() => writeMode.status?.mode ?? "draft");

onMounted(() => {
  unregisterSensitiveStore = registerSensitiveStore(writeMode);
  void writeMode.load();
});

onBeforeUnmount(() => {
  unregisterSensitiveStore?.();
  unregisterSensitiveStore = null;
  writeMode.resetSensitive();
});

const setMode = async (next: McpWriteMode): Promise<void> => {
  if (await writeMode.setMode(next)) {
    message.success(
      next === "direct" ? "MCP 直接写入已开启。" : "MCP 已恢复草稿模式。",
    );
  } else {
    message.error(writeMode.error?.message ?? "写入模式切换失败。");
  }
};
</script>

<template>
  <section class="settings-section">
    <header>
      <div>
        <h2>MCP 写入模式</h2>
        <p>模式仅对 MCP 正式文档写入生效，重置于当前 Vault 会话。</p>
      </div>
      <NIcon
        :component="mode === 'direct' ? ShieldAlert : ShieldCheck"
        size="22"
        aria-hidden="true"
        :class="mode === 'direct' ? 'mode-icon mode-icon--direct' : 'mode-icon'"
      />
    </header>

    <NAlert v-if="writeMode.error" type="error" :show-icon="false">
      {{ writeMode.error.message }}
    </NAlert>

    <div v-if="writeMode.loading && !writeMode.status" class="mode-loading">
      <NSpin size="small" />
      <span>正在读取模式…</span>
    </div>
    <template v-else>
      <NButtonGroup class="mode-control">
        <NButton
          :type="mode === 'draft' ? 'primary' : 'default'"
          :loading="writeMode.changing"
          @click="setMode('draft')"
        >
          <template #icon>
            <NIcon :component="ShieldCheck" aria-hidden="true" />
          </template>
          草稿模式
        </NButton>
        <NPopconfirm
          :on-positive-click="() => setMode('direct')"
          positive-text="确认开启"
          negative-text="取消"
        >
          <template #trigger>
            <NButton
              :type="mode === 'direct' ? 'warning' : 'default'"
              :loading="writeMode.changing"
            >
              <template #icon>
                <NIcon :component="ShieldAlert" aria-hidden="true" />
              </template>
              直接写入
            </NButton>
          </template>
          开启后，所有持有当前 MCP Token 的客户端都可以直接修改正式知识。
          该状态会在锁定、超时、会话变化或服务重启后恢复为草稿模式。
        </NPopconfirm>
      </NButtonGroup>
      <dl class="mode-meta">
        <dt>当前状态</dt>
        <dd>{{ mode === "direct" ? "直接写入" : "草稿模式" }}</dd>
        <dt>是否生效</dt>
        <dd>{{ writeMode.status?.effective ? "是" : "否" }}</dd>
        <dt>Session 代次</dt>
        <dd>{{ writeMode.status?.sessionGeneration ?? "—" }}</dd>
      </dl>
    </template>
  </section>
</template>

<style scoped>
.settings-section header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
}

.mode-icon {
  color: var(--cv-accent);
}

.mode-icon--direct {
  color: var(--cv-warning);
}

.mode-loading {
  display: flex;
  align-items: center;
  gap: 10px;
  color: var(--cv-muted);
}

.mode-control {
  justify-self: start;
}

.mode-meta {
  display: grid;
  grid-template-columns: 120px minmax(0, 1fr);
  gap: 8px 12px;
  margin: 0;
  font-size: 12px;
}

.mode-meta dt {
  color: var(--cv-muted);
}

.mode-meta dd {
  margin: 0;
}
</style>
