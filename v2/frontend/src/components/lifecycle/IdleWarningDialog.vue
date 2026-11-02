<script setup lang="ts">
import { ref } from "vue";
import { useRouter } from "vue-router";
import { NButton, NModal } from "naive-ui";
import { useSessionStore } from "../../stores/session";

const session = useSessionStore();
const router = useRouter();
const refreshing = ref(false);
const locking = ref(false);

const continueWorking = async (): Promise<void> => {
  refreshing.value = true;
  try {
    await session.touchActivity();
  } catch {
    await session.refreshStatus().catch(() => undefined);
  } finally {
    refreshing.value = false;
  }
};

const lockNow = async (): Promise<void> => {
  if (locking.value) {
    return;
  }

  locking.value = true;
  try {
    await session.lock();
    await router.replace({ name: "unlock" });
  } catch {
    await session.refreshStatus().catch(() => undefined);
  } finally {
    locking.value = false;
  }
};
</script>

<template>
  <NModal
    :show="session.showIdleWarning"
    :mask-closable="false"
    :close-on-esc="false"
    transform-origin="center"
  >
    <section
      class="idle-dialog"
      role="alertdialog"
      aria-modal="true"
      aria-labelledby="idle-title"
    >
      <p class="idle-kicker">即将自动锁定</p>
      <h2 id="idle-title">
        {{ session.idleRemainingSeconds ?? 0 }} 秒后锁定 Vault
      </h2>
      <p>
        锁定后当前页面中的正文、草稿和搜索结果会被清除。请先保存未完成的内容。
      </p>
      <div class="idle-actions">
        <NButton :loading="locking" @click="lockNow"> 立即锁定 </NButton>
        <NButton type="primary" :loading="refreshing" @click="continueWorking">
          继续使用
        </NButton>
      </div>
    </section>
  </NModal>
</template>

<style scoped>
.idle-dialog {
  width: min(92vw, 440px);
  padding: 24px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  box-shadow: 0 24px 80px rgb(10 20 14 / 24%);
}

.idle-kicker {
  margin: 0 0 8px;
  color: var(--cv-warning);
  font-size: 12px;
  font-weight: 700;
}

.idle-dialog h2 {
  margin: 0;
  font-size: 1.35rem;
  letter-spacing: 0;
}

.idle-dialog p:not(.idle-kicker) {
  margin: 12px 0 24px;
  color: var(--cv-muted);
  line-height: 1.65;
}

.idle-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
