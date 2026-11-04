<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from "vue";
import cytoscape, { type Core } from "cytoscape";
import type { GraphEdge, GraphNode } from "../../types/api";
import { toGraphElements } from "../../utils/graph-elements";

const props = defineProps<{
  nodes: GraphNode[];
  edges: GraphEdge[];
}>();
const emit = defineEmits<{
  open: [documentId: string];
}>();

const host = ref<HTMLDivElement | null>(null);
let graph: Core | null = null;

const render = (): void => {
  if (!host.value) {
    return;
  }
  graph?.destroy();
  graph = cytoscape({
    container: host.value,
    elements: toGraphElements(props.nodes, props.edges),
    wheelSensitivity: 0.2,
    style: [
      {
        selector: "node",
        style: {
          "background-color": "#2f6f62",
          color: "#202521",
          label: "data(label)",
          "font-size": 11,
          "text-valign": "bottom",
          "text-margin-y": 7,
          width: 22,
          height: 22,
          "border-width": 1,
          "border-color": "#ffffff",
        },
      },
      {
        selector: "node[center = 1]",
        style: {
          "background-color": "#a96118",
          width: 30,
          height: 30,
          "font-size": 12,
          "font-weight": 600,
        },
      },
      {
        selector: "edge",
        style: {
          width: "mapData(weight, 1, 5, 1, 3)",
          "line-color": "#a3aca6",
          "line-style": "solid",
          "target-arrow-color": "#a3aca6",
          "target-arrow-shape": "triangle",
          "curve-style": "bezier",
          label: "data(label)",
          "font-size": 9,
          color: "#626b66",
          "text-background-color": "#ffffff",
          "text-background-opacity": 0.8,
          "text-background-padding": "2px",
          opacity: 0.65,
        },
      },
      {
        selector: 'edge[edgeKind = "typed_relation"]',
        style: {
          "line-color": "#8a5a2b",
          "line-style": "dashed",
          "target-arrow-color": "#8a5a2b",
        },
      },
      {
        selector: "edge[directed = 0]",
        style: {
          "target-arrow-shape": "none",
        },
      },
    ],
    layout: {
      name: "breadthfirst",
      directed: true,
      spacingFactor: 1.25,
      animate: false,
    },
  });
  graph.on("tap", "node", (event) => {
    emit("open", event.target.id());
  });
};

onMounted(render);
watch(() => [props.nodes, props.edges] as const, render, { deep: true });
onBeforeUnmount(() => {
  graph?.destroy();
  graph = null;
});
</script>

<template>
  <div ref="host" class="graph-canvas" />
</template>

<style scoped>
.graph-canvas {
  width: 100%;
  min-height: 560px;
  border: 1px solid var(--cv-border);
  border-radius: 8px;
  background: var(--cv-surface);
}

@media (max-width: 699px) {
  .graph-canvas {
    min-height: 420px;
  }
}
</style>
