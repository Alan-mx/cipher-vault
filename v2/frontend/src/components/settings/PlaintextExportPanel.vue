<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import { NAlert, NButton, NIcon, NInput } from "naive-ui";
import { FileDown, ShieldAlert } from "@lucide/vue";
import { exportApi } from "../../api/exports";
import {
  registerSensitiveStore,
  type SensitiveStore,
} from "../../session/lifecycle";
import { useSessionStore } from "../../stores/session";
import { describeApiError } from "../../utils/api-error";

const confirmationPhrase = "导出明文";
const session = useSessionStore();
const confirmation = ref("");
const exporting = ref(false);
const errorMessage = ref("");
const downloadNotice = ref("");
const canExport = computed(
  () => confirmation.value === confirmationPhrase && !exporting.value,
);

const requestExport = async (): Promise<void> => {
  errorMessage.value = "";
  downloadNotice.value = "";
  if (!canExport.value) {
    errorMessage.value = `请输入“${confirmationPhrase}”确认导出。`;
    return;
  }
  if (
    !window.confirm(
      "导出的 ZIP 是未加密明文。任何拿到文件的人都能直接读取全部文档，确定继续吗？",
    )
  ) {
    return;
  }

  exporting.value = true;
  try {
    const preflight = await exportApi.preflight(confirmationPhrase);
    const csrfToken = await session.ensureCsrf();
    exportApi.submitPlaintextExport(preflight.ticket, csrfToken);
    downloadNotice.value =
      "下载请求已提交，请查看浏览器下载列表；页面无法确认文件是否已完整下载。";
  } catch (error) {
    errorMessage.value = describeApiError(error);
  } finally {
    exporting.value = false;
  }
};

const resetSensitive = (): void => {
  confirmation.value = "";
  exporting.value = false;
  errorMessage.value = "";
  downloadNotice.value = "";
};

let unregisterSensitiveStore: (() => void) | null = null;

onMounted(() => {
  const store: SensitiveStore = { resetSensitive };
  unregisterSensitiveStore = registerSensitiveStore(store);
});

onBeforeUnmount(() => {
  unregisterSensitiveStore?.();
  unregisterSensitiveStore = null;
  resetSensitive();
});
</script>

<template>
  <section class="settings-section export-panel">
    <header>
      <div>
        <h2>导出明文</h2>
        <p>下载当前全部有效 Markdown/TXT 文档，不包含回收站和历史 Revision。</p>
      </div>
    </header>

    <NAlert type="warning" :show-icon="false">
      <template #header>
        <span class="export-warning-title">
          <NIcon :component="ShieldAlert" aria-hidden="true" />
          下载后将失去 Vault 加密保护
        </span>
      </template>
      ZIP 内的正文、Frontmatter 和文件路径均为明文。不要上传到不受信任的位置。
      浏览器和 MCP 均不会把 ZIP 内容保存在 CipherVault 中。
    </NAlert>

    <NAlert v-if="errorMessage" type="error" :show-icon="false">
      {{ errorMessage }}
    </NAlert>
    <NAlert v-if="downloadNotice" type="info" :show-icon="false">
      {{ downloadNotice }}
    </NAlert>

    <div class="export-confirmation">
      <label for="plaintext-export-confirmation">
        输入 <code>{{ confirmationPhrase }}</code> 确认
      </label>
      <NInput
        id="plaintext-export-confirmation"
        v-model:value="confirmation"
        autocomplete="off"
        :disabled="exporting"
        :placeholder="confirmationPhrase"
      />
    </div>

    <NButton
      type="error"
      secondary
      :disabled="!canExport"
      :loading="exporting"
      @click="requestExport"
    >
      <template #icon>
        <NIcon :component="FileDown" aria-hidden="true" />
      </template>
      下载明文 ZIP
    </NButton>
  </section>
</template>

<style scoped>
.export-panel {
  border-color: color-mix(in srgb, var(--cv-danger) 35%, var(--cv-border));
}

.export-warning-title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.export-confirmation {
  display: grid;
  gap: 6px;
  max-width: 420px;
  font-size: 12px;
}

.export-confirmation label {
  color: var(--cv-muted);
}

.export-panel :deep(code) {
  padding: 2px 5px;
  border-radius: 4px;
  background: var(--cv-bg);
}
</style>
