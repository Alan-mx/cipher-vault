<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import { useRoute } from "vue-router";
import {
  NConfigProvider,
  NMessageProvider,
  darkTheme,
  dateZhCN,
  zhCN,
  type GlobalThemeOverrides,
} from "naive-ui";
import WorkspaceLayout from "./layouts/WorkspaceLayout.vue";

const prefersDark = window.matchMedia("(prefers-color-scheme: dark)");
const followsDark = ref(prefersDark.matches);
const theme = computed(() => (followsDark.value ? darkTheme : null));
const lightThemeOverrides: GlobalThemeOverrides = {
  common: {
    bodyColor: "#f4f5f2",
    cardColor: "#ffffff",
    modalColor: "#ffffff",
    popoverColor: "#ffffff",
    textColorBase: "#202521",
    textColor1: "#202521",
    textColor2: "#4f5852",
    textColor3: "#646e67",
    placeholderColor: "#68716b",
    borderColor: "#d8ddd8",
    primaryColor: "#2f6f62",
    primaryColorHover: "#3d8173",
    primaryColorPressed: "#24584e",
  },
  Message: {
    color: "#ffffff",
    textColor: "#202521",
    iconColor: "#2f6f62",
  },
  Empty: {
    textColor: "#68716b",
    iconColor: "#68716b",
  },
};
const darkThemeOverrides: GlobalThemeOverrides = {
  common: {
    bodyColor: "#171a18",
    cardColor: "#202420",
    modalColor: "#202420",
    popoverColor: "#202420",
    textColorBase: "#eef1ed",
    textColor1: "#eef1ed",
    textColor2: "#c0c8c2",
    textColor3: "#a3aca6",
    placeholderColor: "#a3aca6",
    borderColor: "#343b36",
    primaryColor: "#68b6a4",
    primaryColorHover: "#7ac7b5",
    primaryColorPressed: "#4e9d8b",
  },
  Message: {
    color: "#202420",
    textColor: "#eef1ed",
    iconColor: "#68b6a4",
  },
  Empty: {
    textColor: "#a3aca6",
    iconColor: "#a3aca6",
  },
};
const themeOverrides = computed(() =>
  followsDark.value ? darkThemeOverrides : lightThemeOverrides,
);
const route = useRoute();
const isWorkspace = computed(() => route.meta.layout === "workspace");

const updateTheme = (event: MediaQueryListEvent) => {
  followsDark.value = event.matches;
};

onMounted(() => {
  prefersDark.addEventListener("change", updateTheme);
});

onBeforeUnmount(() => {
  prefersDark.removeEventListener("change", updateTheme);
});
</script>

<template>
  <NConfigProvider
    :theme="theme"
    :theme-overrides="themeOverrides"
    :locale="zhCN"
    :date-locale="dateZhCN"
  >
    <NMessageProvider>
      <WorkspaceLayout v-if="isWorkspace">
        <RouterView />
      </WorkspaceLayout>
      <RouterView v-else />
    </NMessageProvider>
  </NConfigProvider>
</template>
