<script setup lang="ts">
import { computed, ref } from "vue";
import { useRouter } from "vue-router";
import {
  NAlert,
  NButton,
  NCheckbox,
  NForm,
  NFormItem,
  NInput,
  useMessage,
} from "naive-ui";
import AuthLayout from "../layouts/AuthLayout.vue";
import { useSessionStore } from "../stores/session";
import { describeApiError } from "../utils/api-error";
import { isValidNewPassword, passwordScalarLength } from "../utils/password";

const router = useRouter();
const session = useSessionStore();
const message = useMessage();
const password = ref("");
const confirmation = ref("");
const riskAccepted = ref(false);
const submitting = ref(false);
const errorMessage = ref("");

const passwordTooShort = computed(
  () => password.value.length > 0 && !isValidNewPassword(password.value),
);
const canSubmit = computed(
  () =>
    isValidNewPassword(password.value) &&
    password.value === confirmation.value &&
    riskAccepted.value &&
    !submitting.value,
);

const submit = async (): Promise<void> => {
  errorMessage.value = "";
  if (!isValidNewPassword(password.value)) {
    errorMessage.value = `密码至少需要 12 个字符，当前 ${passwordScalarLength(password.value)} 个。`;
    return;
  }
  if (password.value !== confirmation.value) {
    errorMessage.value = "两次输入的密码不一致。";
    return;
  }
  if (!riskAccepted.value) {
    errorMessage.value = "请确认密码丢失后无法恢复。";
    return;
  }

  submitting.value = true;
  try {
    await session.setup(password.value, riskAccepted.value);
    password.value = "";
    confirmation.value = "";
    message.success("Vault 已创建，请重新输入密码解锁。");
    await router.replace({ name: "unlock" });
  } catch (error) {
    errorMessage.value = describeApiError(error);
  } finally {
    submitting.value = false;
  }
};
</script>

<template>
  <AuthLayout
    eyebrow="首次使用"
    title="创建本地 Vault"
    description="选择一个只有你知道的密码。Vault 数据不会离开当前设备。"
  >
    <NAlert v-if="errorMessage" type="error" :show-icon="false">
      {{ errorMessage }}
    </NAlert>

    <NForm class="auth-form" @submit.prevent="submit">
      <NFormItem label="Vault 密码">
        <NInput
          v-model:value="password"
          type="password"
          show-password-on="click"
          placeholder="至少 12 个字符"
          autofocus
          v-bind="passwordTooShort ? { status: 'warning' } : {}"
        />
      </NFormItem>
      <p v-if="passwordTooShort" class="field-hint">
        建议使用较长的随机口令或密码短语。
      </p>

      <NFormItem label="确认密码">
        <NInput
          v-model:value="confirmation"
          type="password"
          show-password-on="click"
          placeholder="再次输入密码"
        />
      </NFormItem>

      <NCheckbox v-model:checked="riskAccepted" class="risk-check">
        我已了解：忘记密码后，Vault 和加密备份都无法恢复。
      </NCheckbox>

      <NButton
        type="primary"
        block
        attr-type="submit"
        :loading="submitting"
        :disabled="!canSubmit"
      >
        创建 Vault
      </NButton>
    </NForm>
  </AuthLayout>
</template>

<style scoped>
.auth-form {
  display: grid;
  gap: 16px;
  margin-top: 20px;
}

.field-hint {
  margin: -10px 0 0;
  color: var(--cv-warning);
  font-size: 12px;
}

.risk-check {
  line-height: 1.6;
  white-space: normal;
}
</style>
