<script setup lang="ts">
import { ref, watch } from "vue";
import { useRouter } from "vue-router";
import { NButton, NEmpty, NTabPane, NTabs, NTag } from "naive-ui";
import { useDocumentStore } from "../../stores/document";
import RelatedPanel from "./RelatedPanel.vue";
import RevisionPanel from "./RevisionPanel.vue";

const documents = useDocumentStore();
const router = useRouter();
const activeTab = ref("outline");

const openDocument = async (documentId: string): Promise<void> => {
  await router.push({ name: "document", params: { id: documentId } });
};

const handleTabChange = (value: string): void => {
  if (value === "related") {
    void documents.loadRelated();
  }
};

watch(
  () => documents.documentId,
  (documentId) => {
    if (documentId && activeTab.value === "related") {
      void documents.loadRelated();
    }
  },
);
</script>

<template>
  <aside class="document-inspector" aria-label="文档知识信息">
    <NTabs
      v-model:value="activeTab"
      type="line"
      animated
      size="small"
      @update:value="handleTabChange"
    >
      <NTabPane name="outline" tab="大纲">
        <div v-if="documents.map?.headings.length" class="inspector-list">
          <div
            v-for="heading in documents.map.headings"
            :key="`${heading.level}:${heading.path.join('/')}`"
            class="heading-row"
            :style="{ paddingLeft: `${(heading.level - 1) * 10}px` }"
          >
            {{ heading.text }}
          </div>
        </div>
        <NEmpty v-else description="没有标题" size="small" />

        <template v-if="documents.map?.frontMatterFields.length">
          <h3>Frontmatter</h3>
          <dl class="frontmatter-list">
            <template
              v-for="field in documents.map.frontMatterFields"
              :key="field.key"
            >
              <dt>{{ field.key }}</dt>
              <dd>{{ field.valueJson }}</dd>
            </template>
          </dl>
        </template>
        <p
          v-if="
            documents.map?.headingsTruncated ||
            documents.map?.frontMatterTruncated
          "
          class="truncated-note"
        >
          部分元数据因服务器上限被截断。
        </p>
      </NTabPane>

      <NTabPane
        name="links"
        :tab="`链接 ${documents.map?.outgoingLinks.length ?? 0}`"
      >
        <div v-if="documents.map?.outgoingLinks.length" class="link-list">
          <button
            v-for="link in documents.map.outgoingLinks"
            :key="`${link.startOffset}:${link.endOffset}`"
            type="button"
            :disabled="!link.target"
            @click="link.target && openDocument(link.target.documentId)"
          >
            <div>
              <strong>{{ link.displayText || link.rawTarget }}</strong>
              <NTag
                size="tiny"
                :type="
                  link.resolutionStatus === 'resolved'
                    ? 'success'
                    : link.resolutionStatus === 'ambiguous'
                      ? 'warning'
                      : 'default'
                "
                :bordered="false"
              >
                {{ link.resolutionStatus }}
              </NTag>
            </div>
            <p v-if="link.target">
              {{ link.target.virtualPath }}
            </p>
            <p v-else-if="link.candidates.length">
              候选：{{ link.candidates.map((item) => item.title).join("、") }}
            </p>
          </button>
        </div>
        <NEmpty v-else description="没有出链" size="small" />
        <p v-if="documents.map?.linksTruncated" class="truncated-note">
          出链列表已截断。
        </p>
      </NTabPane>

      <NTabPane
        name="backlinks"
        :tab="`反链 ${documents.map?.backlinkCount ?? 0}`"
      >
        <div v-if="documents.backlinks.length" class="link-list">
          <button
            v-for="item in documents.backlinks"
            :key="item.sourceDocumentId"
            type="button"
            @click="openDocument(item.sourceDocumentId)"
          >
            <strong>{{ item.sourceTitle }}</strong>
            <p>{{ item.sourceVirtualPath }}</p>
            <span
              >{{ item.occurrenceCount }} 处引用 · R{{
                item.sourceRevision
              }}</span
            >
          </button>
        </div>
        <NEmpty v-else description="暂无反向链接" size="small" />
        <NButton
          v-if="documents.backlinkCursor"
          block
          size="tiny"
          secondary
          @click="documents.loadBacklinks(true)"
        >
          加载更多
        </NButton>
      </NTabPane>

      <NTabPane name="mentions" tab="提及">
        <div v-if="documents.mentions.length" class="mention-list">
          <button
            v-for="item in documents.mentions"
            :key="item.sourceDocumentId"
            type="button"
            @click="openDocument(item.sourceDocumentId)"
          >
            <strong>{{ item.sourceTitle }}</strong>
            <p
              v-for="occurrence in item.occurrences"
              :key="occurrence.startOffset"
            >
              {{ occurrence.context }}
            </p>
          </button>
        </div>
        <NEmpty v-else description="暂无纯文本提及" size="small" />
        <NButton
          v-if="documents.mentionCursor"
          block
          size="tiny"
          secondary
          @click="documents.loadMentions(true)"
        >
          加载更多
        </NButton>
      </NTabPane>

      <NTabPane name="related" tab="相关">
        <RelatedPanel />
      </NTabPane>

      <NTabPane name="revisions" tab="版本">
        <RevisionPanel />
      </NTabPane>
    </NTabs>
  </aside>
</template>

<style scoped>
.document-inspector {
  min-width: 0;
  padding: 16px;
  border-left: 1px solid var(--cv-border);
  background: color-mix(in srgb, var(--cv-surface) 64%, transparent);
  overflow: auto;
}

.inspector-list {
  display: grid;
  gap: 4px;
}

.heading-row {
  overflow: hidden;
  padding-top: 4px;
  padding-bottom: 4px;
  color: var(--cv-muted);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.document-inspector h3 {
  margin: 24px 0 8px;
  font-size: 12px;
}

.frontmatter-list {
  display: grid;
  gap: 4px;
  margin: 0;
}

.frontmatter-list dt {
  color: var(--cv-muted);
  font-size: 11px;
}

.frontmatter-list dd {
  overflow: hidden;
  margin: 0 0 8px;
  font-family: "Cascadia Code", Consolas, monospace;
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.link-list,
.mention-list {
  display: grid;
  gap: 4px;
}

.link-list button,
.mention-list button {
  display: grid;
  gap: 4px;
  width: 100%;
  padding: 9px 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: inherit;
  text-align: left;
}

.link-list button:hover:not(:disabled),
.mention-list button:hover {
  background: color-mix(in srgb, var(--cv-accent) 7%, transparent);
}

.link-list button:disabled {
  cursor: default;
}

.link-list button div {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.link-list button strong,
.mention-list button strong {
  overflow: hidden;
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.link-list button p,
.mention-list button p {
  display: -webkit-box;
  overflow: hidden;
  margin: 0;
  color: var(--cv-muted);
  font-size: 11px;
  line-height: 1.5;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}

.link-list button span {
  color: var(--cv-muted);
  font-size: 10px;
}

.truncated-note {
  margin: 12px 0 0;
  color: var(--cv-warning);
  font-size: 11px;
}
</style>
