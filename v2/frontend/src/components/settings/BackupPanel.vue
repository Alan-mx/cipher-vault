<script setup lang="ts">
import { computed, onMounted } from "vue";
import { NAlert, NButton, NIcon, NSpin } from "naive-ui";
import { Archive, Download, Plus, RefreshCw } from "@lucide/vue";
import { useBackupStore } from "../../stores/backups";
import type { BackupFileStatus, BackupListItem } from "../../types/api";
import { describeApiError } from "../../utils/api-error";

const backups = useBackupStore();
const errorMessage = computed(() =>
  backups.error ? describeApiError(backups.error) : "",
);

const statusLabel = (status: BackupFileStatus): string => {
  switch (status) {
    case "verified":
      return "已验证";
    case "manifest_readable":
      return "可读取";
    default:
      return "无效";
  }
};

const formatBytes = (bytes: number): string => {
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  if (bytes < 1024 * 1024) {
    return `${(bytes / 1024).toFixed(1)} KiB`;
  }
  if (bytes < 1024 * 1024 * 1024) {
    return `${(bytes / (1024 * 1024)).toFixed(1)} MiB`;
  }
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GiB`;
};

const formatDate = (item: BackupListItem): string => {
  const value = item.createdAtUtc ?? item.modifiedAtUtc;
  return new Date(value).toLocaleString("zh-CN", {
    dateStyle: "medium",
    timeStyle: "short",
  });
};

const createBackup = async (): Promise<void> => {
  await backups.create();
};

onMounted(() => {
  void backups.load();
});
</script>

<template>
  <section class="settings-section backup-panel">
    <header class="backup-heading">
      <div>
        <h2>加密备份</h2>
        <p>创建、下载并验证加密备份；恢复需停止 Server 后离线执行。</p>
      </div>
      <NButton
        type="primary"
        :loading="backups.creating"
        :disabled="backups.creating"
        @click="createBackup"
      >
        <template #icon>
          <NIcon :component="Plus" aria-hidden="true" />
        </template>
        创建备份
      </NButton>
    </header>

    <NAlert v-if="errorMessage" type="error" :show-icon="false">
      {{ errorMessage }}
    </NAlert>

    <div v-if="backups.loading" class="backup-loading" aria-live="polite">
      <NSpin size="small" />
      正在读取备份…
    </div>

    <div v-else-if="backups.items.length === 0" class="backup-empty">
      <NIcon :component="Archive" size="22" aria-hidden="true" />
      <span>还没有可用的加密备份。</span>
    </div>

    <div v-else class="backup-list">
      <article
        v-for="item in backups.items"
        :key="item.fileName"
        class="backup-row"
      >
        <div class="backup-main">
          <code>{{ item.fileName }}</code>
          <div class="backup-meta">
            <span>{{ formatBytes(item.bytes) }}</span>
            <span>{{ formatDate(item) }}</span>
            <span
              class="backup-status"
              :class="`backup-status--${item.status}`"
            >
              {{ statusLabel(item.status) }}
            </span>
            <span v-if="item.errorCode" class="backup-error-code">
              {{ item.errorCode }}
            </span>
          </div>
        </div>
        <a
          class="backup-download"
          :href="backups.downloadUrl(item.fileName)"
          :download="item.fileName"
          target="_blank"
          rel="noopener noreferrer"
        >
          <NIcon :component="Download" aria-hidden="true" />
          下载
        </a>
      </article>
    </div>

    <NButton
      v-if="backups.nextCursor"
      secondary
      :loading="backups.loadingMore"
      @click="backups.loadMore"
    >
      <template #icon>
        <NIcon :component="RefreshCw" aria-hidden="true" />
      </template>
      加载更多
    </NButton>

    <div class="restore-guide">
      <h3>离线恢复</h3>
      <p>
        停止正常 Server
        后，在同一机器或恢复设备上执行以下维护命令。密码只会从交互式终端读取。
      </p>
      <code class="command-block">
        CipherVault.Server --restore-backup &lt;backup-file&gt; --target
        &lt;new-directory&gt;
      </code>
      <p>
        目标目录必须不存在；备份目录通过 <code>CIPHERVAULT_BACKUP_DIR</code>
        配置，Web 不显示服务器绝对路径。
      </p>
      <p>Docker 使用同一镜像：</p>
      <code class="command-block">
        docker run --rm -it -v /host/backups:/backups:ro -v
        /host/restore-parent:/restore CipherVault.Server --restore-backup
        /backups/&lt;file&gt; --target /restore/&lt;new-directory&gt;
      </code>
    </div>
  </section>
</template>

<style scoped>
.backup-panel {
  min-width: 0;
}

.backup-heading {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}

.backup-loading,
.backup-empty {
  display: flex;
  min-height: 90px;
  align-items: center;
  justify-content: center;
  gap: 10px;
  color: var(--cv-muted);
  font-size: 12px;
}

.backup-list {
  display: grid;
  border-top: 1px solid var(--cv-border);
}

.backup-row {
  display: flex;
  min-width: 0;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 12px 0;
  border-bottom: 1px solid var(--cv-border);
}

.backup-main {
  display: grid;
  min-width: 0;
  gap: 6px;
}

.backup-main code {
  overflow-wrap: anywhere;
  font-size: 12px;
}

.backup-meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  color: var(--cv-muted);
  font-size: 11px;
}

.backup-error-code {
  color: var(--cv-danger);
}

.backup-status {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  color: var(--cv-text);
  font-weight: 600;
}

.backup-status::before {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--cv-muted);
  content: "";
}

.backup-status--verified::before {
  background: #0b7a45;
}

.backup-status--manifest_readable::before {
  background: #8a5d00;
}

.backup-status--invalid::before {
  background: var(--cv-danger);
}

.backup-download {
  display: inline-flex;
  flex: 0 0 auto;
  align-items: center;
  gap: 5px;
  color: var(--cv-accent);
  font-size: 12px;
  text-decoration: none;
}

.backup-download:hover,
.backup-download:focus-visible {
  text-decoration: underline;
}

.restore-guide {
  display: grid;
  gap: 8px;
  padding-top: 4px;
}

.restore-guide h3 {
  margin: 0;
  font-size: 0.95rem;
}

.restore-guide p {
  margin: 0;
}

.command-block {
  display: block;
  overflow-x: auto;
  padding: 10px 12px;
  border-radius: 6px;
  background: var(--cv-bg);
  color: var(--cv-text);
  font-size: 11px;
  line-height: 1.6;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

@media (max-width: 600px) {
  .backup-heading {
    align-items: stretch;
    flex-direction: column;
  }

  .backup-row {
    align-items: flex-start;
    flex-direction: column;
  }
}
</style>
