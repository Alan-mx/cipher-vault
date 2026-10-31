<script setup lang="ts">
defineProps<{
  eyebrow: string;
  title: string;
  description: string;
}>();
</script>

<template>
  <main class="auth-page">
    <div class="auth-frame">
      <header class="auth-brand">
        <div class="auth-mark" aria-hidden="true">CV</div>
        <div>
          <strong>CipherVault</strong>
          <span>本地加密知识库</span>
        </div>
      </header>

      <section class="auth-content">
        <p class="auth-eyebrow">
          {{ eyebrow }}
        </p>
        <h1>{{ title }}</h1>
        <p class="auth-description">
          {{ description }}
        </p>
        <slot />
      </section>

      <p class="auth-footnote">数据保留在当前设备，不经过第三方服务。</p>
    </div>
  </main>
</template>

<style scoped>
.auth-page {
  display: grid;
  min-height: 100vh;
  padding: 24px;
  background:
    linear-gradient(var(--cv-border) 1px, transparent 1px) 0 0 / 100% 64px,
    var(--cv-bg);
  place-items: center;
}

.auth-frame {
  width: min(100%, 460px);
  padding: 28px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: color-mix(in srgb, var(--cv-surface) 94%, transparent);
  box-shadow: 0 20px 60px rgb(25 31 27 / 8%);
}

.auth-brand {
  display: flex;
  align-items: center;
  gap: 12px;
}

.auth-brand div:last-child {
  display: grid;
  gap: 2px;
}

.auth-brand strong {
  font-size: 15px;
}

.auth-brand span {
  color: var(--cv-muted);
  font-size: 12px;
}

.auth-mark {
  display: grid;
  width: 42px;
  height: 42px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  color: var(--cv-accent);
  font-size: 12px;
  font-weight: 700;
  place-items: center;
}

.auth-content {
  margin-top: 44px;
}

.auth-eyebrow {
  margin: 0 0 8px;
  color: var(--cv-accent);
  font-size: 12px;
  font-weight: 700;
}

.auth-content h1 {
  margin: 0;
  font-size: 2rem;
  font-weight: 650;
  letter-spacing: 0;
}

.auth-description {
  margin: 12px 0 28px;
  color: var(--cv-muted);
  line-height: 1.7;
}

.auth-footnote {
  margin: 32px 0 0;
  color: var(--cv-muted);
  font-size: 12px;
}

@media (max-width: 560px) {
  .auth-page {
    padding: 0;
    background: var(--cv-bg);
  }

  .auth-frame {
    min-height: 100vh;
    padding: 24px;
    border: 0;
    border-radius: 0;
    box-shadow: none;
  }
}
</style>
