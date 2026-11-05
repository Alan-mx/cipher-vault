<script setup lang="ts">
import type { DraftValidationIssue } from "../../types/knowledge-base";
import { draftFieldId } from "../../utils/draft-editor";

defineProps<{
  issues: DraftValidationIssue[];
}>();
</script>

<template>
  <section v-if="issues.length" class="issues" aria-label="校验问题">
    <h3>校验问题</h3>
    <ul>
      <li
        v-for="(issue, index) in issues"
        :key="`${issue.fieldPath}-${index}`"
        :class="`severity-${issue.severity}`"
      >
        <a
          v-if="issue.fieldPath !== '$'"
          :href="`#${draftFieldId(issue.fieldPath)}`"
        >
          {{ issue.fieldPath }}
        </a>
        <code v-else>$</code>
        <span>{{ issue.message }}</span>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.issues {
  margin-top: 18px;
  padding: 12px;
  border: 1px solid var(--cv-border);
  border-radius: 7px;
  background: var(--cv-bg);
}
h3 {
  margin: 0 0 8px;
  font-size: 15px;
}
ul {
  display: grid;
  gap: 6px;
  margin: 0;
  padding-left: 18px;
}
li {
  display: grid;
  gap: 2px;
}
a {
  color: inherit;
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
}
.severity-error {
  color: #b93f36;
}
.severity-warning {
  color: #9a6700;
}
</style>
