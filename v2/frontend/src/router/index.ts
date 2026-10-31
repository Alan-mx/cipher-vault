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
      path: "/documents",
      name: "documents",
      component: () => import("../views/DocumentsView.vue"),
      meta: {
        vault: "unlocked",
        sensitive: true,
        layout: "workspace",
        title: "知识库",
      },
    },
    {
      path: "/documents/:id",
      name: "document",
      component: () => import("../views/DocumentView.vue"),
      meta: {
        vault: "unlocked",
        sensitive: true,
        layout: "workspace",
        title: "文档",
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
      path: "/trash",
      name: "trash",
      component: () => import("../views/TrashView.vue"),
      meta: {
        vault: "unlocked",
        sensitive: true,
        layout: "workspace",
        title: "回收站",
      },
    },
    {
      path: "/tools/import",
      name: "import",
      component: () => import("../views/ImportView.vue"),
      meta: {
        vault: "unlocked",
        sensitive: true,
        layout: "workspace",
        title: "导入",
      },
    },
    {
      path: "/audit",
      name: "audit",
      component: () => import("../views/AuditView.vue"),
      meta: {
        vault: "unlocked",
        sensitive: true,
        layout: "workspace",
        title: "活动记录",
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
