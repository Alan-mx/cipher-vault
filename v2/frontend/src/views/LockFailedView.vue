<script setup lang="ts">
import { NAlert } from "naive-ui";
import AuthLayout from "../layouts/AuthLayout.vue";
</script>

<template>
  <AuthLayout
    eyebrow="需要人工处理"
    title="Vault 锁定失败"
    description="服务无法安全释放当前数据库会话。为避免继续暴露数据，本页面不提供重试解锁。"
  >
    <NAlert type="error" :show-icon="false">
      请关闭并重新启动 CipherVault.Server，然后重新打开浏览器页面。
    </NAlert>
  </AuthLayout>
</template>
