<script setup lang="ts">
import { computed, ref } from "vue";
import { useRouter } from "vue-router";
import {
  NAlert,
  NButton,
  NForm,
  NFormItem,
  NInput,
  useMessage,
} from "naive-ui";
import McpTokenPanel from "../components/settings/McpTokenPanel.vue";
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
    oldPassword.value = "";
    newPassword.value = "";
    confirmation.value = "";
    session.updateStatus(status);
    session.clearSensitive("password_changed");
    message.success("密码已修改，Vault 已锁定。");
    await router.replace({ name: "unlock" });
  } catch (error) {
    passwordError.value = describeApiError(error);
  }
};
</script>

<template>
  <section class="settings-view">
    <header class="page-heading">
      <p>配置</p>
      <h1>设置</h1>
    </header>

    <section class="settings-section">
      <header>
        <div>
          <h2>运行配置</h2>
          <p>配置通过环境变量和 Server 重启完成。</p>
        </div>
      </header>
      <dl class="config-list">
        <dt>闲置锁定</dt>
        <dd>{{ session.status?.idleTimeoutMinutes ?? "—" }} 分钟</dd>
        <dt>闲置时间变量</dt>
        <dd><code>CIPHERVAULT_IDLE_TIMEOUT_MINUTES</code></dd>
        <dt>Vault 目录变量</dt>
        <dd><code>CIPHERVAULT_DATA_DIR</code></dd>
      </dl>
      <p class="muted-note">出于安全边界，页面不显示真实服务器文件路径。</p>
    </section>

    <section class="settings-section">
      <header>
        <div>
          <h2>修改 Vault 密码</h2>
          <p>修改成功后 Vault 会立即锁定，所有旧 Web Cookie 失效。</p>
        </div>
      </header>
      <NAlert v-if="passwordError" type="error" :show-icon="false">
        {{ passwordError }}
      </NAlert>
      <NForm class="password-form" @submit.prevent="changePassword">
        <NFormItem label="旧密码">
          <NInput
            v-model:value="oldPassword"
            type="password"
            show-password-on="click"
          />
        </NFormItem>
        <NFormItem label="新密码">
          <NInput
            v-model:value="newPassword"
            type="password"
            show-password-on="click"
            placeholder="至少 12 个字符"
          />
        </NFormItem>
        <NFormItem label="确认新密码">
          <NInput
            v-model:value="confirmation"
            type="password"
            show-password-on="click"
          />
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
    </section>

    <McpTokenPanel />

    <section class="settings-section">
      <header>
        <div>
          <h2>加密备份</h2>
          <p>备份创建、下载和离线恢复将在 Phase 7 实现。</p>
        </div>
      </header>
      <NAlert type="info" :show-icon="false">
        当前阶段不提供备份按钮，也不会调用不存在的备份 API。
      </NAlert>
    </section>
  </section>
</template>

<style scoped>
.settings-view {
  display: grid;
  width: min(100%, 900px);
  gap: 16px;
  padding: 32px 40px 80px;
}

.page-heading p {
  margin: 0 0 5px;
  color: var(--cv-muted);
  font-size: 12px;
}

.page-heading h1 {
  margin: 0;
  font-size: 2rem;
  letter-spacing: 0;
}

.settings-section {
  display: grid;
  gap: 14px;
  padding: 20px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}

.settings-section h2 {
  margin: 0;
  font-size: 1.1rem;
}

.settings-section p {
  margin: 4px 0 0;
  color: var(--cv-muted);
  font-size: 12px;
  line-height: 1.55;
}

.config-list {
  display: grid;
  grid-template-columns: 150px minmax(0, 1fr);
  gap: 8px 12px;
  margin: 0;
  font-size: 12px;
}

.config-list dt {
  color: var(--cv-muted);
}

.config-list dd {
  margin: 0;
}

.config-list code {
  padding: 2px 5px;
  border-radius: 4px;
  background: var(--cv-bg);
}

.muted-note {
  margin: 0;
}

.password-form {
  display: grid;
  gap: 4px;
  max-width: 520px;
}

@media (max-width: 899px) {
  .settings-view {
    padding: 24px 20px 64px;
  }
}
</style>
