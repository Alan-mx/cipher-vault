<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { NAlert, NButton, NIcon, NSpin } from "naive-ui";
import { Check, Copy } from "@lucide/vue";
import { mcpApi } from "../api/mcp";
import McpTokenPanel from "../components/settings/McpTokenPanel.vue";
import type { McpInfo } from "../types/api";
import { buildMcpConfig } from "../utils/mcp-config";
import { describeApiError } from "../utils/api-error";

const info = ref<McpInfo | null>(null);
const error = ref<string | null>(null);
const copied = ref(false);

const endpoint = computed(() =>
  info.value ? `${window.location.origin}${info.value.endpointPath}` : "",
);
const config = computed(() =>
  info.value
    ? buildMcpConfig(window.location.origin, info.value.endpointPath)
    : "",
);
const toolGroups = computed(() => {
  const groups = new Map<string, string[]>();
  for (const tool of info.value?.tools ?? []) {
    const names = groups.get(tool.group) ?? [];
    names.push(tool.name);
    groups.set(tool.group, names);
  }
  return [...groups.entries()];
});

const load = async (): Promise<void> => {
  try {
    info.value = await mcpApi.info();
  } catch (caught) {
    error.value = describeApiError(caught);
  }
};

const copyConfig = async (): Promise<void> => {
  if (!config.value) return;
  try {
    await navigator.clipboard.writeText(config.value);
    copied.value = true;
    window.setTimeout(() => {
      copied.value = false;
    }, 2000);
  } catch {
    copied.value = false;
  }
};

onMounted(load);
</script>

<template>
  <section class="page">
    <header class="page-header">
      <div>
        <p class="eyebrow">MCP</p>
        <h1>MCP 接入</h1>
      </div>
    </header>

    <p v-if="error" class="error">{{ error }}</p>
    <div v-else-if="!info" class="loading">
      <NSpin size="small" />
      正在读取接入信息
    </div>

    <template v-else>
      <section class="panel">
        <h2>连接</h2>
        <div class="definition">
          <span>传输</span>
          <code>{{ info.transport }}</code>
        </div>
        <div class="definition">
          <span>地址</span>
          <code>{{ endpoint }}</code>
        </div>
        <div class="definition">
          <span>鉴权</span>
          <code>Authorization: Bearer &lt;MCP_TOKEN&gt;</code>
        </div>
        <NAlert type="info" :show-icon="false">
          Vault 需保持解锁。MCP 只能创建、修改、提交或放弃 Draft，批准和拒绝只在
          Web AI 工作区。
        </NAlert>
        <pre>{{ config }}</pre>
        <NButton
          type="primary"
          secondary
          :disabled="!config"
          @click="copyConfig"
        >
          <template #icon>
            <NIcon :component="copied ? Check : Copy" aria-hidden="true" />
          </template>
          {{ copied ? "已复制" : "复制配置" }}
        </NButton>
      </section>

      <McpTokenPanel />

      <section class="panel">
        <h2>Tools</h2>
        <dl class="capabilities">
          <div v-for="[group, names] in toolGroups" :key="group">
            <dt>{{ group }}</dt>
            <dd>
              <code v-for="name in names" :key="name">{{ name }}</code>
            </dd>
          </div>
        </dl>
      </section>

      <section class="panel">
        <h2>Prompts</h2>
        <dl class="capabilities">
          <div v-for="prompt in info.prompts" :key="prompt.name">
            <dt>{{ prompt.title }}</dt>
            <dd>
              <code>{{ prompt.name }}</code>
              <span>{{ prompt.summary }}</span>
            </dd>
          </div>
        </dl>
      </section>
    </template>
  </section>
</template>

<style scoped>
.page {
  max-width: 1000px;
  margin: 0 auto;
  padding: 28px;
}
.page-header {
  margin-bottom: 20px;
}
.eyebrow {
  margin: 0 0 6px;
  color: var(--cv-accent);
  font-size: 11px;
  text-transform: uppercase;
}
h1 {
  margin: 0 0 18px;
  font-size: 28px;
}
h2 {
  margin: 0 0 12px;
  font-size: 15px;
}
.panel {
  display: grid;
  gap: 12px;
  margin-bottom: 14px;
  padding: 16px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}
.definition {
  display: grid;
  gap: 4px;
}
.definition span {
  color: var(--cv-muted);
  font-size: 12px;
}
code,
pre {
  overflow: auto;
  border: 1px solid var(--cv-border);
  border-radius: 6px;
  background: color-mix(in srgb, var(--cv-bg) 65%, var(--cv-surface));
  color: var(--cv-text);
  font-family: ui-monospace, "Cascadia Code", Consolas, monospace;
}
.definition code {
  padding: 7px 9px;
  font-size: 12px;
  overflow-wrap: anywhere;
}
pre {
  padding: 12px;
  font-size: 12px;
  line-height: 1.6;
  white-space: pre-wrap;
}
.capabilities {
  display: grid;
  gap: 10px;
  margin: 0;
}
.capabilities dt {
  color: var(--cv-muted);
  font-size: 12px;
  font-weight: 650;
}
.capabilities dd {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  align-items: center;
  margin: 4px 0 0;
}
.capabilities dd code {
  padding: 3px 6px;
  border: 1px solid var(--cv-border);
}
.capabilities span {
  color: var(--cv-muted);
  font-size: 12px;
}
.loading,
.error {
  display: grid;
  min-height: 180px;
  place-content: center;
  gap: 10px;
  color: var(--cv-muted);
}
.error {
  color: #b93f36;
}
@media (max-width: 700px) {
  .page {
    padding: 18px;
  }
}
</style>
