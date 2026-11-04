<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from "vue";
import {
  NAlert,
  NButton,
  NIcon,
  NPopconfirm,
  NSpin,
  NTag,
  useMessage,
} from "naive-ui";
import { RefreshCw, ShieldAlert, ShieldCheck } from "@lucide/vue";
import { useSemanticIndexStore } from "../../stores/semantic-index";
import { registerSensitiveStore } from "../../session/lifecycle";
import type { SemanticIndexState } from "../../types/api";

type TagType = "default" | "error" | "primary" | "info" | "success" | "warning";

const semantic = useSemanticIndexStore();
const message = useMessage();
const profile = computed(() => semantic.status?.profile ?? null);
const isRemote = computed(() => profile.value?.isRemote === true);
const canGrant = computed(
  () =>
    semantic.status?.remoteDeploymentAllowed === true &&
    semantic.status.remoteConsentRequired &&
    !semantic.status.remoteConsentGranted &&
    profile.value !== null,
);
const canRevoke = computed(
  () =>
    semantic.status?.remoteConsentGranted === true && profile.value !== null,
);
const canRebuild = computed(
  () =>
    profile.value !== null &&
    semantic.status?.state !== "building" &&
    (!isRemote.value || semantic.status?.remoteConsentGranted === true),
);
const stateLabel = computed(
  () => stateLabels[semantic.status?.state ?? "not_configured"],
);
const stateType = computed<TagType>(
  () => stateTypes[semantic.status?.state ?? "not_configured"],
);

const refresh = async (): Promise<void> => {
  await semantic.load();
};

const grant = async (): Promise<void> => {
  if (await semantic.grantRemoteConsent()) {
    message.success("远程语义索引已确认。");
  } else {
    message.error(semantic.error?.message ?? "远程语义索引确认失败。");
  }
};

const revoke = async (): Promise<void> => {
  if (await semantic.revokeRemoteConsent()) {
    message.success("远程语义索引确认已撤销。");
  } else {
    message.error(semantic.error?.message ?? "撤销远程语义索引失败。");
  }
};

const rebuild = async (): Promise<void> => {
  if (await semantic.rebuild()) {
    message.success("语义索引重建已开始。");
  } else {
    message.error(semantic.error?.message ?? "语义索引重建失败。");
  }
};

let unregisterSensitiveStore: (() => void) | null = null;

onMounted(() => {
  unregisterSensitiveStore = registerSensitiveStore(semantic);
  void semantic.load();
});

onBeforeUnmount(() => {
  unregisterSensitiveStore?.();
  unregisterSensitiveStore = null;
  semantic.resetSensitive();
});

const stateLabels: Record<SemanticIndexState, string> = {
  not_configured: "未配置",
  disabled: "已停用",
  building: "构建中",
  ready: "可用",
  degraded: "部分可用",
  failed: "失败",
};

const stateTypes: Record<SemanticIndexState, TagType> = {
  not_configured: "default",
  disabled: "warning",
  building: "info",
  ready: "success",
  degraded: "warning",
  failed: "error",
};
</script>

<template>
  <section class="settings-section semantic-panel">
    <header>
      <div>
        <h2>语义检索</h2>
        <p>
          Embedding 配置由环境变量提供；本页只管理索引状态、远程确认与重建。
        </p>
      </div>
      <NButton
        quaternary
        circle
        aria-label="刷新语义索引状态"
        :loading="semantic.loading"
        @click="refresh"
      >
        <template #icon>
          <NIcon :component="RefreshCw" aria-hidden="true" />
        </template>
      </NButton>
    </header>

    <NAlert v-if="semantic.error" type="error" :show-icon="false">
      {{ semantic.error.message }}
    </NAlert>

    <div v-if="semantic.loading && !semantic.status" class="status-loading">
      <NSpin size="small" />
      <span>正在读取语义索引状态…</span>
    </div>

    <template v-else-if="semantic.status">
      <div class="state-row">
        <NTag :type="stateType" :bordered="false">
          {{ stateLabel }}
        </NTag>
        <span v-if="semantic.status.endpointDisplay" class="endpoint">
          {{ semantic.status.endpointDisplay }}
        </span>
      </div>

      <NAlert
        v-if="semantic.status.state === 'not_configured'"
        type="info"
        :show-icon="false"
      >
        未配置 Embedding Provider。关键词搜索不受影响。
      </NAlert>

      <NAlert
        v-else-if="isRemote && !semantic.status.remoteConsentGranted"
        type="warning"
        :show-icon="true"
      >
        <template #icon>
          <NIcon :component="ShieldAlert" aria-hidden="true" />
        </template>
        启用前必须确认：文档分块和搜索查询可能发送到远程 Embedding
        服务；远程服务的数据保留策略不由 CipherVault 控制。
      </NAlert>

      <dl v-if="profile" class="semantic-meta">
        <dt>Provider</dt>
        <dd>{{ profile.providerId }}</dd>
        <dt>模型</dt>
        <dd>{{ profile.modelId }}</dd>
        <dt>部署</dt>
        <dd>{{ isRemote ? "远程" : "本地" }}</dd>
        <dt>维度 / 输入上限</dt>
        <dd>{{ profile.dimension }} / {{ profile.maxInputTokens }}</dd>
        <dt>Profile</dt>
        <dd>
          <code class="fingerprint">{{ profile.profileId }}</code>
        </dd>
        <dt>端点指纹</dt>
        <dd>
          <code class="fingerprint">{{ profile.endpointFingerprint }}</code>
        </dd>
        <dt>文档 / Chunk</dt>
        <dd>
          {{ semantic.status.documentCount }} / {{ semantic.status.chunkCount }}
        </dd>
        <dt>待处理 / 失败</dt>
        <dd>
          {{ semantic.status.pendingJobCount }} /
          {{ semantic.status.failedJobCount }}
        </dd>
        <dt>最后更新</dt>
        <dd>
          {{
            semantic.status.lastUpdatedAt
              ? new Date(semantic.status.lastUpdatedAt).toLocaleString("zh-CN")
              : "—"
          }}
        </dd>
        <dt v-if="semantic.status.lastErrorCode">最后错误</dt>
        <dd v-if="semantic.status.lastErrorCode">
          <code>{{ semantic.status.lastErrorCode }}</code>
        </dd>
      </dl>

      <div v-if="profile" class="action-row">
        <NPopconfirm
          v-if="canGrant"
          positive-text="确认允许"
          negative-text="取消"
          @positive-click="grant"
        >
          <template #trigger>
            <NButton
              type="warning"
              secondary
              :loading="semantic.activeAction === 'grant'"
              :disabled="semantic.mutating && semantic.activeAction !== 'grant'"
            >
              <template #icon>
                <NIcon :component="ShieldAlert" aria-hidden="true" />
              </template>
              确认远程语义索引
            </NButton>
          </template>
          确认后，文档分块与搜索查询可能发送到当前远程端点。
        </NPopconfirm>

        <NPopconfirm
          v-if="canRevoke"
          positive-text="确认撤销"
          negative-text="取消"
          @positive-click="revoke"
        >
          <template #trigger>
            <NButton
              secondary
              :loading="semantic.activeAction === 'revoke'"
              :disabled="
                semantic.mutating && semantic.activeAction !== 'revoke'
              "
            >
              撤销远程确认
            </NButton>
          </template>
          撤销后新的远程语义查询和索引任务会停止。
        </NPopconfirm>

        <NPopconfirm
          v-if="canRebuild"
          positive-text="确认重建"
          negative-text="取消"
          @positive-click="rebuild"
        >
          <template #trigger>
            <NButton
              secondary
              :loading="semantic.activeAction === 'rebuild'"
              :disabled="
                semantic.mutating && semantic.activeAction !== 'rebuild'
              "
            >
              <template #icon>
                <NIcon :component="ShieldCheck" aria-hidden="true" />
              </template>
              重建语义索引
            </NButton>
          </template>
          当前 Profile 的向量会失效，并重新索引全部活动文档。
        </NPopconfirm>
      </div>
    </template>
  </section>
</template>

<style scoped>
.settings-section {
  display: grid;
  gap: 14px;
  padding: 20px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}

.settings-section header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
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

.status-loading {
  display: flex;
  align-items: center;
  gap: 10px;
  color: var(--cv-muted);
}

.state-row {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 10px;
}

.endpoint {
  overflow: hidden;
  color: var(--cv-muted);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.semantic-meta {
  display: grid;
  grid-template-columns: 130px minmax(0, 1fr);
  gap: 8px 12px;
  margin: 0;
  font-size: 12px;
}

.semantic-meta dt {
  color: var(--cv-muted);
}

.semantic-meta dd {
  min-width: 0;
  margin: 0;
}

.fingerprint {
  overflow-wrap: anywhere;
  word-break: break-all;
}

.action-row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

@media (max-width: 699px) {
  .semantic-meta {
    grid-template-columns: 1fr;
  }

  .semantic-meta dd {
    margin: -4px 0 4px;
  }
}
</style>
