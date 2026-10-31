<script setup lang="ts">
import { ref } from "vue";
import { NAlert, NButton, NInput, NModal, useMessage } from "naive-ui";

const props = defineProps<{
  show: boolean;
  token: string;
}>();
const emit = defineEmits<{
  "update:show": [value: boolean];
  close: [];
}>();

const message = useMessage();
const copied = ref(false);

const copy = async (): Promise<void> => {
  try {
    await navigator.clipboard.writeText(props.token);
    copied.value = true;
    message.success("Token 已复制。");
  } catch {
    message.warning("无法访问剪贴板，请手动选择复制。");
  }
};

const close = (): void => {
  emit("update:show", false);
  emit("close");
};
</script>

<template>
  <NModal :show="show" :mask-closable="false">
    <section
      class="token-dialog"
      role="dialog"
      aria-modal="true"
      aria-labelledby="token-title"
    >
      <p class="token-kicker">只显示一次</p>
      <h2 id="token-title">MCP Token 已轮换</h2>
      <NAlert type="warning" :show-icon="false">
        Token 拥有全部已开放 MCP 工具的读取、修改和删除权限。轮换后旧 Token
        已立即失效。
      </NAlert>
      <NInput
        class="token-value"
        :value="token"
        readonly
        @focus="($event.target as HTMLInputElement).select()"
      />
      <p class="clipboard-note">
        复制后剪贴板可能继续保留 Token。关闭此弹窗后无法再次查看。
      </p>
      <div class="token-actions">
        <NButton @click="close"> 我已保存，关闭 </NButton>
        <NButton type="primary" @click="copy">
          {{ copied ? "再次复制" : "复制 Token" }}
        </NButton>
      </div>
    </section>
  </NModal>
</template>

<style scoped>
.token-dialog {
  display: grid;
  width: min(94vw, 620px);
  gap: 16px;
  padding: 24px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}

.token-kicker {
  margin: 0;
  color: var(--cv-warning);
  font-size: 12px;
  font-weight: 700;
}

.token-dialog h2 {
  margin: 0;
  font-size: 1.4rem;
}

.token-value {
  font-family: "Cascadia Code", Consolas, monospace;
}

.clipboard-note {
  margin: 0;
  color: var(--cv-muted);
  font-size: 12px;
  line-height: 1.55;
}

.token-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
