<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from "vue";
import { basicSetup } from "codemirror";
import { EditorState } from "@codemirror/state";
import { EditorView } from "@codemirror/view";
import { markdown } from "@codemirror/lang-markdown";
import type { VaultFormat } from "../../types/domain";

const props = defineProps<{
  modelValue: string;
  format: VaultFormat;
}>();
const emit = defineEmits<{
  "update:modelValue": [value: string];
}>();

const host = ref<HTMLDivElement | null>(null);
let view: EditorView | null = null;

const createState = (value: string): EditorState =>
  EditorState.create({
    doc: value,
    extensions: [
      basicSetup,
      ...(props.format === "md" ? [markdown()] : []),
      EditorView.lineWrapping,
      EditorView.contentAttributes.of({
        "aria-label": "文档正文",
        "aria-multiline": "true",
      }),
      EditorView.updateListener.of((update) => {
        if (update.docChanged) {
          emit("update:modelValue", update.state.doc.toString());
        }
      }),
      EditorView.theme({
        "&": {
          minHeight: "60vh",
          backgroundColor: "var(--cv-surface)",
          color: "var(--cv-text)",
          fontSize: "14px",
        },
        ".cm-content": {
          fontFamily: '"Cascadia Code", "SFMono-Regular", Consolas, monospace',
          lineHeight: "1.7",
          padding: "20px",
        },
        ".cm-gutters": {
          backgroundColor: "var(--cv-bg)",
          color: "var(--cv-muted)",
          borderRight: "1px solid var(--cv-border)",
        },
        "&.cm-focused": {
          outline:
            "2px solid color-mix(in srgb, var(--cv-accent) 45%, transparent)",
        },
      }),
    ],
  });

onMounted(() => {
  if (!host.value) {
    return;
  }
  view = new EditorView({
    state: createState(props.modelValue),
    parent: host.value,
  });
});

watch(
  () => props.modelValue,
  (value) => {
    if (!view || view.state.doc.toString() === value) {
      return;
    }
    view.dispatch({
      changes: {
        from: 0,
        to: view.state.doc.length,
        insert: value,
      },
    });
  },
);

watch(
  () => props.format,
  (format) => {
    if (!view) {
      return;
    }
    view.setState(createState(view.state.doc.toString()));
    if (format === "md") {
      // Configuration is embedded in the recreated state.
    }
  },
);

onBeforeUnmount(() => {
  view?.destroy();
  view = null;
});
</script>

<template>
  <div ref="host" class="document-code-editor" />
</template>

<style scoped>
.document-code-editor {
  min-height: 60vh;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  overflow: hidden;
}
</style>
