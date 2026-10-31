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
import AuthLayout from "../layouts/AuthLayout.vue";
import { useSessionStore } from "../stores/session";
import { describeApiError } from "../utils/api-error";

const router = useRouter();
const session = useSessionStore();
const message = useMessage();
const password = ref("");
const submitting = ref(false);
const errorMessage = ref("");
const canSubmit = computed(
  () => password.value.length > 0 && !submitting.value,
);

const reasonMessage = computed(() => {
  if (
    session.clearReason === "idle_lock" ||
    session.status?.lockReason === "idle_timeout"
  ) {
    return "Vault 因长时间未操作已自动锁定。";
  }
  if (session.clearReason === "password_changed") {
    return "密码已修改，请使用新密码重新解锁。";
  }
  if (session.clearReason === "session_expired") {
    return "会话已失效，请重新解锁。";
  }
  return "";
});

const submit = async (): Promise<void> => {
  if (!canSubmit.value) {
    return;
  }
  errorMessage.value = "";
  submitting.value = true;
  try {
    await session.unlock(password.value);
    message.success("Vault 已解锁。");
    await router.replace({ name: "documents" });
  } catch (error) {
    errorMessage.value = describeApiError(error);
  } finally {
    password.value = "";
    submitting.value = false;
  }
};
</script>

<template>
  <AuthLayout
    eyebrow="Vault 已锁定"
    title="解锁 CipherVault"
    description="输入 Vault 密码后，Web 与 MCP 会共享同一个解锁会话。"
  >
    <NAlert
      v-if="reasonMessage"
      class="session-reason"
      type="warning"
      :show-icon="false"
    >
      {{ reasonMessage }}
    </NAlert>
    <NAlert v-if="errorMessage" type="error" :show-icon="false">
      {{ errorMessage }}
    </NAlert>

    <NForm class="auth-form" @submit.prevent="submit">
      <NFormItem label="Vault 密码">
        <NInput
          v-model:value="password"
          type="password"
          show-password-on="click"
          placeholder="输入密码"
          autofocus
        />
      </NFormItem>
      <NButton
        type="primary"
        block
        attr-type="submit"
        :loading="submitting"
        :disabled="!canSubmit"
      >
        解锁
      </NButton>
    </NForm>
  </AuthLayout>
</template>

<style scoped>
.session-reason {
  margin-bottom: 12px;
}

.auth-form {
  display: grid;
  gap: 16px;
  margin-top: 20px;
}
</style>
