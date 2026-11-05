import { createRouter, createWebHistory } from "vue-router";

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: "/",
      name: "boot",
      component: () => import("../views/BootView.vue"),
      meta: {
        vault: "any",
        sensitive: false,
        layout: "standalone",
        title: "CipherVault",
      },
    },
    {
      path: "/setup",
      name: "setup",
      component: () => import("../views/SetupView.vue"),
      meta: {
        vault: "uninitialized",
        sensitive: false,
        layout: "auth",
        title: "初始化 Vault",
      },
    },
    {
      path: "/unlock",
      name: "unlock",
      component: () => import("../views/UnlockView.vue"),
      meta: {
        vault: "locked",
        sensitive: false,
        layout: "auth",
        title: "解锁 Vault",
      },
    },
    {
      path: "/lock-failed",
      name: "lock-failed",
      component: () => import("../views/LockFailedView.vue"),
      meta: {
        vault: "any",
        sensitive: false,
        layout: "auth",
        title: "Vault 需要重启",
      },
    },
    {
      path: "/home",
      name: "home",
      component: () => import("../views/HomeView.vue"),
      meta: {
        vault: "unlocked",
        sensitive: true,
        layout: "workspace",
        title: "首页",
      },
    },
    {
      path: "/knowledge",
      name: "knowledge",
      component: () => import("../views/KnowledgeLibraryView.vue"),
      meta: {
        vault: "unlocked",
        sensitive: true,
        layout: "workspace",
        title: "知识库",
      },
    },
    {
      path: "/knowledge/:id",
      name: "knowledge-detail",
      component: () => import("../views/KnowledgeDetailView.vue"),
      meta: {
        vault: "unlocked",
        sensitive: true,
        layout: "workspace",
        title: "知识",
      },
    },
    {
      path: "/inbox",
      name: "inbox",
      component: () => import("../views/InboxView.vue"),
      meta: {
        vault: "unlocked",
        sensitive: true,
        layout: "workspace",
        title: "收集箱",
      },
    },
    {
      path: "/sources",
      name: "sources",
      component: () => import("../views/SourcesView.vue"),
      meta: {
        vault: "unlocked",
        sensitive: true,
        layout: "workspace",
        title: "来源",
      },
    },
    {
      path: "/graph",
      name: "graph",
      component: () => import("../views/GraphView.vue"),
      meta: {
        vault: "unlocked",
        sensitive: true,
        layout: "workspace",
        title: "知识网络",
      },
    },
    {
      path: "/review",
      name: "review",
      component: () => import("../views/UnifiedReviewView.vue"),
      meta: {
        vault: "unlocked",
        sensitive: true,
        layout: "workspace",
        title: "AI 工作区",
      },
    },
    {
      path: "/settings/knowledge-model",
      name: "knowledge-model",
      component: () => import("../views/KnowledgeModelView.vue"),
      meta: {
        vault: "unlocked",
        sensitive: true,
        layout: "workspace",
        title: "知识模型",
      },
    },
    {
      path: "/settings/mcp",
      name: "mcp",
      component: () => import("../views/McpSetupView.vue"),
      meta: {
        vault: "unlocked",
        sensitive: false,
        layout: "workspace",
        title: "MCP 接入",
      },
    },
    {
      path: "/settings",
      name: "settings",
      component: () => import("../views/SettingsView.vue"),
      meta: {
        vault: "unlocked",
        sensitive: true,
        layout: "workspace",
        title: "设置",
      },
    },
    {
      path: "/:pathMatch(.*)*",
      name: "not-found",
      component: () => import("../views/PlaceholderView.vue"),
      meta: {
        vault: "any",
        sensitive: false,
        layout: "standalone",
        title: "页面不存在",
      },
    },
  ],
});
