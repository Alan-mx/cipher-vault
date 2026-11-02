<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from "vue";
import { NAlert, NButton, NPopconfirm, NSpin, useMessage } from "naive-ui";
import { useSettingsStore } from "../../stores/settings";
import { registerSensitiveStore } from "../../session/lifecycle";
import McpTokenDialog from "./McpTokenDialog.vue";

const settings = useSettingsStore();
const message = useMessage();
const dialogOpen = ref(false);
const rawToken = ref("");

const rotate = async (): Promise<void> => {
  try {
    const result = await settings.rotateMcpToken();
    rawToken.value = result.token;
    dialogOpen.value = true;
  } catch {
    message.error(settings.error?.message ?? "Token 轮换失败。");
  }
};

const clearToken = (): void => {
  rawToken.value = "";
};

const resetSensitive = (): void => {
  dialogOpen.value = false;
  clearToken();
};

let unregisterSensitiveStore: (() => void) | null = null;

onMounted(() => {
  unregisterSensitiveStore = registerSensitiveStore({ resetSensitive });
  void settings.loadMcpStatus();
});

onBeforeUnmount(() => {
  unregisterSensitiveStore?.();
  unregisterSensitiveStore = null;
  resetSensitive();
});
</script>

<template>
  <section class="settings-section">
    <header>
      <div>
        <h2>MCP Token</h2>
        <p>供本机 AI 客户端连接 `/mcp` 的全局 Bearer 凭据。</p>
      </div>
      <NSpin v-if="settings.loadingMcp" size="small" />
    </header>

    <NAlert
      v-if="settings.mcpStatus?.configured"
      type="success"
      :show-icon="false"
    >
      已配置 · Version {{ settings.mcpStatus.version ?? 1 }}
      <template v-if="settings.mcpStatus.rotatedAtUtc">
        · 轮换于
        {{ new Date(settings.mcpStatus.rotatedAtUtc).toLocaleString("zh-CN") }}
      </template>
    </NAlert>
    <NAlert v-else type="warning" :show-icon="false">
      尚未配置 MCP Token。生成后只显示一次。
    </NAlert>

    <div class="token-warning">
      Token 泄露意味着知识库可能被读取和修改；Vault 锁定时 Token 不能解锁
      Vault。
    </div>
    <NPopconfirm @positive-click="rotate">
      <template #trigger>
        <NButton type="primary" secondary>
          {{ settings.mcpStatus?.configured ? "轮换 Token" : "生成 Token" }}
        </NButton>
      </template>
      轮换后旧 Token 立即失效，所有已配置客户端都需要更新。
    </NPopconfirm>

    <McpTokenDialog
      v-model:show="dialogOpen"
      :token="rawToken"
      @close="clearToken"
    />
  </section>
</template>

<style scoped>
.settings-section {
  display: grid;
  gap: 12px;
  padding: 20px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}

.settings-section header {
  display: flex;
  justify-content: space-between;
  gap: 12px;
}

.settings-section h2 {
  margin: 0;
  font-size: 1.1rem;
}

.settings-section p {
  margin: 4px 0 0;
  color: var(--cv-muted);
  font-size: 12px;
}

.token-warning {
  color: var(--cv-muted);
  font-size: 12px;
  line-height: 1.6;
}
</style>
