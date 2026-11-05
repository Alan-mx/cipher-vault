<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import { RouterLink, useRouter } from "vue-router";
import {
  NAlert,
  NButton,
  NForm,
  NFormItem,
  NInput,
  useMessage,
} from "naive-ui";
import BackupPanel from "../components/settings/BackupPanel.vue";
import McpTokenPanel from "../components/settings/McpTokenPanel.vue";
import { registerSensitiveStore } from "../session/lifecycle";
import { useSessionStore } from "../stores/session";
import { useSettingsStore } from "../stores/settings";
import { describeApiError } from "../utils/api-error";
import { isValidNewPassword } from "../utils/password";

const session = useSessionStore();
const settings = useSettingsStore();
const router = useRouter();
const message = useMessage();
const oldPassword = ref("");
const newPassword = ref("");
const confirmation = ref("");
const passwordError = ref("");
const canChangePassword = computed(
  () =>
    oldPassword.value.length > 0 &&
    isValidNewPassword(newPassword.value) &&
    newPassword.value === confirmation.value &&
    !settings.changingPassword,
);

const resetSensitive = (): void => {
  oldPassword.value = "";
  newPassword.value = "";
  confirmation.value = "";
  passwordError.value = "";
};

const changePassword = async (): Promise<void> => {
  passwordError.value = "";
  if (!canChangePassword.value) {
    passwordError.value = "请检查旧密码、新密码长度和两次输入。";
    return;
  }
  try {
    const status = await settings.changePassword(
      oldPassword.value,
      newPassword.value,
    );
    resetSensitive();
    session.updateStatus(status);
    session.clearSensitive("password_changed");
    message.success("密码已修改，Vault 已锁定。");
    await router.replace({ name: "unlock" });
  } catch (error) {
    passwordError.value = describeApiError(error);
  }
};

let unregisterSensitiveStore: (() => void) | null = null;

onMounted(() => {
  unregisterSensitiveStore = registerSensitiveStore({ resetSensitive });
});

onBeforeUnmount(() => {
  unregisterSensitiveStore?.();
  unregisterSensitiveStore = null;
  resetSensitive();
});
</script>

<template>
  <section class="page">
    <header>
      <p class="eyebrow">Settings</p>
      <h1>设置</h1>
    </header>
    <div class="grid">
      <article>
        <h2>知识模型</h2>
        <p>维护类型、属性、模板、关系和来源规则。</p>
        <RouterLink to="/settings/knowledge-model">打开模型维护</RouterLink>
      </article>
      <article>
        <h2>修改 Vault 密码</h2>
        <p>修改成功后 Vault 会立即锁定。</p>
        <NAlert v-if="passwordError" type="error" :show-icon="false">
          {{ passwordError }}
        </NAlert>
        <NForm class="password-form" @submit.prevent="changePassword">
          <NFormItem label="旧密码">
            <NInput v-model:value="oldPassword" type="password" />
          </NFormItem>
          <NFormItem label="新密码">
            <NInput v-model:value="newPassword" type="password" />
          </NFormItem>
          <NFormItem label="确认新密码">
            <NInput v-model:value="confirmation" type="password" />
          </NFormItem>
          <NButton
            type="primary"
            attr-type="submit"
            :disabled="!canChangePassword"
            :loading="settings.changingPassword"
          >
            修改密码并锁定
          </NButton>
        </NForm>
      </article>
      <article>
        <h2>MCP</h2>
        <RouterLink to="/settings/mcp">打开 MCP 接入</RouterLink>
        <McpTokenPanel />
      </article>
      <article>
        <h2>加密备份</h2>
        <BackupPanel />
      </article>
    </div>
  </section>
</template>

<style scoped>
.page {
  max-width: 1000px;
  margin: 0 auto;
  padding: 28px;
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
.grid {
  display: grid;
  gap: 14px;
}
article {
  padding: 16px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}
h2 {
  margin: 0 0 8px;
  font-size: 15px;
}
p {
  color: var(--cv-muted);
}
a {
  color: var(--cv-accent);
}
.password-form {
  display: grid;
  max-width: 440px;
  margin-top: 12px;
}
@media (max-width: 700px) {
  .page {
    padding: 18px;
  }
}
</style>
