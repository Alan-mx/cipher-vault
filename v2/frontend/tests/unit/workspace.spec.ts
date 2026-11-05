import { mount } from "@vue/test-utils";
import { createPinia } from "pinia";
import { createMemoryHistory, createRouter } from "vue-router";
import { describe, expect, it } from "vitest";
import WorkspaceLayout from "../../src/layouts/WorkspaceLayout.vue";

describe("WorkspaceLayout", () => {
  it("renders the primary local knowledge navigation", async () => {
    const pinia = createPinia();
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: "/", name: "workspace-test", component: { template: "" } },
      ],
    });
    await router.push("/");
    await router.isReady();
    const wrapper = mount(WorkspaceLayout, {
      global: {
        plugins: [pinia, router],
        stubs: {
          RouterView: true,
          NDrawer: true,
          NDrawerContent: true,
          NModal: true,
          NTooltip: true,
        },
      },
      slots: {
        default: '<div data-test="content">workspace</div>',
      },
    });

    const labels = wrapper
      .findAll(".rail-link")
      .map((link) => link.attributes("aria-label"));
    expect(labels).toContain("知识库");
    expect(labels).toContain("整理");
    expect(labels).toContain("设置");
    expect(labels).toContain("锁定 Vault");
    expect(wrapper.find("[data-test='content']").exists()).toBe(true);
  });
});
