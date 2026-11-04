<script setup lang="ts">
import { computed, ref } from "vue";
import {
  NAlert,
  NButton,
  NCheckbox,
  NInput,
  NPopconfirm,
  NSpin,
  useMessage,
} from "naive-ui";
import { useImportStore } from "../stores/import";
import ImportPreviewTable from "../components/import/ImportPreviewTable.vue";

const imports = useImportStore();
const message = useMessage();
const sourceConfirmed = ref(false);
const canPreview = computed(
  () =>
    imports.sourceRoot.trim().length > 0 &&
    sourceConfirmed.value &&
    !imports.loading,
);

const preview = async (): Promise<void> => {
  await imports.runPreview();
  if (imports.error) {
    message.error(imports.error.message);
  }
};

const commit = async (): Promise<void> => {
  await imports.commit();
  if (imports.error) {
    message.error(imports.error.message);
  } else if (imports.commitResult) {
    message.success(
      `导入完成：成功 ${imports.commitResult.imported}，跳过 ${imports.commitResult.skipped}，失败 ${imports.commitResult.failed}。`,
    );
  }
};
</script>

<template>
  <section class="import-view">
    <header class="page-heading">
      <p>批量导入</p>
      <h1>导入 Markdown / TXT</h1>
    </header>

    <NAlert type="warning" :show-icon="false">
      导入源目录由服务器进程读取，而不是浏览器。该功能只允许本机 loopback
      会话使用。
    </NAlert>

    <section class="import-source">
      <label for="import-source-root">服务器可见的源目录</label>
      <NInput
        id="import-source-root"
        v-model:value="imports.sourceRoot"
        placeholder="例如 D:\Notes 或 /home/user/notes"
      />
      <NCheckbox v-model:checked="sourceConfirmed">
        我确认该目录由服务器读取，并理解导入不会覆盖已有文档。
      </NCheckbox>
      <NButton
        type="primary"
        :disabled="!canPreview"
        :loading="imports.loading"
        @click="preview"
      >
        生成预览
      </NButton>
    </section>

    <NAlert v-if="imports.error" type="error" :show-icon="false">
      {{ imports.error.message }}
    </NAlert>

    <section v-if="imports.preview" class="preview-section">
      <div class="preview-summary">
        <div>
          <strong>预览已生成</strong>
          <span>
            Ready {{ imports.preview.readyCount }} · Skipped
            {{ imports.preview.skippedCount }} · Rejected
            {{ imports.preview.rejectedCount }}
          </span>
          <span v-if="imports.preview.manifestVersion">
            Manifest v{{ imports.preview.manifestVersion }}
          </span>
          <span>
            预览 Token 有效至
            {{
              new Date(imports.preview.expiresAt).toLocaleTimeString("zh-CN")
            }}
          </span>
        </div>
        <NPopconfirm @positive-click="commit">
          <template #trigger>
            <NButton type="primary" :loading="imports.committing">
              提交导入
            </NButton>
          </template>
          提交会重新读取文件并验证哈希，已有文档不会被覆盖。确定继续吗？
        </NPopconfirm>
      </div>
      <ImportPreviewTable :items="imports.items" />
    </section>

    <section v-if="imports.commitResult" class="commit-result">
      <strong>提交结果</strong>
      <p>
        成功 {{ imports.commitResult.imported }}，跳过
        {{ imports.commitResult.skipped }}，失败
        {{ imports.commitResult.failed }}。
      </p>
      <ul>
        <li v-for="item in imports.commitResult.items" :key="item.relativePath">
          {{ item.relativePath }}：{{ item.result }}
          <span v-if="item.errorCode">（{{ item.errorCode }}）</span>
        </li>
      </ul>
    </section>

    <div v-if="imports.loading" class="loading">
      <NSpin size="small" />
      <span>正在扫描目录…</span>
    </div>
  </section>
</template>

<style scoped>
.import-view {
  width: min(100%, 1080px);
  padding: 32px 40px 80px;
}

.page-heading p {
  margin: 0 0 5px;
  color: var(--cv-muted);
  font-size: 12px;
}

.page-heading h1 {
  margin: 0 0 24px;
  font-size: 2rem;
  letter-spacing: 0;
}

.import-source {
  display: grid;
  gap: 12px;
  margin: 24px 0;
  padding: 20px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}

.import-source label {
  color: var(--cv-muted);
  font-size: 12px;
  font-weight: 650;
}

.preview-summary {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 16px;
}

.preview-summary div {
  display: grid;
  gap: 4px;
}

.preview-summary span,
.commit-result p,
.commit-result li {
  color: var(--cv-muted);
  font-size: 12px;
}

.preview-section,
.commit-result {
  margin-top: 24px;
}

.commit-result ul {
  padding-left: 18px;
}

.loading {
  display: flex;
  gap: 10px;
  align-items: center;
  margin-top: 20px;
  color: var(--cv-muted);
}

@media (max-width: 899px) {
  .import-view {
    padding: 24px 20px 64px;
  }
}
</style>
