<script setup lang="ts">
/* eslint-disable vue/no-v-html */
import { computed } from "vue";
import { useRouter } from "vue-router";
import type { DocumentMap } from "../../types/api";
import { renderMarkdown } from "../../markdown/renderer";

const props = defineProps<{
  markdown: string;
  map: DocumentMap | null;
}>();

const router = useRouter();
const html = computed(() => renderMarkdown(props.markdown, props.map));

const handleClick = (event: MouseEvent): void => {
  const anchor = (event.target as HTMLElement).closest<HTMLAnchorElement>(
    "a.document-link",
  );
  if (!anchor) {
    return;
  }
  const documentId = anchor.dataset.documentId;
  if (!documentId) {
    return;
  }
  event.preventDefault();
  const location = {
    name: "document",
    params: { id: documentId },
  };
  const hash = anchor.hash ? decodeURIComponent(anchor.hash) : "";
  void router.push(hash ? { ...location, hash } : location);
};
</script>

<template>
  <article
    class="markdown-preview"
    aria-label="Markdown 预览"
    @click="handleClick"
    v-html="html"
  />
</template>

<style>
.markdown-preview {
  min-height: 60vh;
  padding: 20px 24px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
  overflow-wrap: anywhere;
}

.markdown-preview h1,
.markdown-preview h2,
.markdown-preview h3 {
  letter-spacing: 0;
}

.markdown-preview pre {
  overflow: auto;
  padding: 14px;
  border-radius: 6px;
  background: var(--cv-bg);
}

.markdown-preview code {
  font-family: "Cascadia Code", "SFMono-Regular", Consolas, monospace;
}

.markdown-preview blockquote {
  margin-left: 0;
  padding-left: 14px;
  border-left: 3px solid var(--cv-accent);
  color: var(--cv-muted);
}

.markdown-preview table {
  width: 100%;
  border-collapse: collapse;
}

.markdown-preview th,
.markdown-preview td {
  padding: 6px 10px;
  border: 1px solid var(--cv-border);
}

.markdown-preview a {
  color: var(--cv-accent);
}

.markdown-preview .document-link {
  text-decoration-thickness: 1px;
  text-underline-offset: 3px;
}

.markdown-image-placeholder {
  display: inline-block;
  padding: 4px 8px;
  border: 1px solid var(--cv-border);
  border-radius: 4px;
  color: var(--cv-muted);
  font-size: 12px;
}
</style>
