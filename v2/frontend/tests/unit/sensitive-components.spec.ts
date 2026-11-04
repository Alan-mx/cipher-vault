/* eslint-disable vue/one-component-per-file */
import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { createMemoryHistory, createRouter } from "vue-router";
import { defineComponent, nextTick } from "vue";
import { NMessageProvider, NPopconfirm } from "naive-ui";
import { afterEach, describe, expect, it, vi } from "vitest";
import { versionApi } from "../../src/api";
import McpTokenPanel from "../../src/components/settings/McpTokenPanel.vue";
import {
  clearSensitiveState,
  resetSessionLifecycleForTest,
} from "../../src/session/lifecycle";
import { useSettingsStore } from "../../src/stores/settings";
import SettingsView from "../../src/views/SettingsView.vue";
import SetupView from "../../src/views/SetupView.vue";
import UnlockView from "../../src/views/UnlockView.vue";

const InputStub = defineComponent({
  props: {
    value: { type: String, default: "" },
    type: { type: String, default: "text" },
  },
  emits: ["update:value"],
  template:
    '<input :type="type" :value="value" @input="$emit(\'update:value\', $event.target.value)" />',
});

const baseStubs = {
  AuthLayout: {
    template: "<main><slot /></main>",
  },
  NAlert: {
    template: "<div><slot /></div>",
  },
  NButton: {
    template: '<button type="button"><slot /></button>',
  },
  NCheckbox: {
    props: ["checked"],
    emits: ["update:checked"],
    template: "<label><slot /></label>",
  },
  NForm: {
    template: "<form><slot /></form>",
  },
  NFormItem: {
    template: "<label><slot /></label>",
  },
  NInput: InputStub,
  NPopconfirm: {
    emits: ["positive-click"],
    template:
      '<div><slot name="trigger" /><button class="confirm" type="button" @click="$emit(\'positive-click\')">confirm</button><slot /></div>',
  },
  NSpin: {
    template: "<span />",
  },
  BackupPanel: {
    template: '<section data-test="backup-panel" />',
  },
  SemanticEmbeddingPanel: {
    template: '<section data-test="semantic-embedding-panel" />',
  },
  PlaintextExportPanel: {
    template: '<section data-test="plaintext-export-panel" />',
  },
};

const settingsStubs = {
  ...baseStubs,
  McpTokenPanel: {
    template: '<section data-test="mcp-token-panel" />',
  },
};

const tokenStubs = {
  ...baseStubs,
  McpTokenDialog: {
    props: ["show", "token"],
    template: "<div data-test=\"raw-token\">{{ show ? token : '' }}</div>",
  },
};

const createRouterFor = async (path: string, name: string) => {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: "/", name: "home", component: { template: "" } },
      { path, name, component: { template: "" } },
      { path: "/unlock", name: "unlock", component: { template: "" } },
    ],
  });
  await router.push(path);
  await router.isReady();
  return router;
};

describe("sensitive component cleanup", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    resetSessionLifecycleForTest();
  });

  it("clears unlock and setup password fields on session clear", async () => {
    const pinia = createPinia();
    const unlockRouter = await createRouterFor("/unlock", "unlock-test");
    const UnlockHost = defineComponent({
      components: { NMessageProvider, UnlockView },
      template: "<NMessageProvider><UnlockView /></NMessageProvider>",
    });
    const unlock = mount(UnlockHost, {
      global: {
        plugins: [pinia, unlockRouter],
        stubs: baseStubs,
      },
    });
    const unlockInput = unlock.get("input");
    await unlockInput.setValue("unlock-secret");

    clearSensitiveState("session_expired");
    await nextTick();

    expect((unlockInput.element as HTMLInputElement).value).toBe("");
    unlock.unmount();

    const setupRouter = await createRouterFor("/setup", "setup-test");
    const SetupHost = defineComponent({
      components: { NMessageProvider, SetupView },
      template: "<NMessageProvider><SetupView /></NMessageProvider>",
    });
    const setup = mount(SetupHost, {
      global: {
        plugins: [pinia, setupRouter],
        stubs: baseStubs,
      },
    });
    const setupInputs = setup.findAll("input");
    await setupInputs[0]!.setValue("setup-secret");
    await setupInputs[1]!.setValue("setup-secret");

    clearSensitiveState("pagehide");
    await nextTick();

    expect((setupInputs[0]!.element as HTMLInputElement).value).toBe("");
    expect((setupInputs[1]!.element as HTMLInputElement).value).toBe("");
    setup.unmount();
  });

  it("clears password-change fields on session clear", async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    vi.spyOn(versionApi, "get").mockResolvedValue({
      productVersion: "2.0.0-rc.1",
      commit: "0123456789abcdef0123456789abcdef01234567",
      rid: "win-x64",
      runtimeVersion: "10.0.13",
      aspNetCoreVersion: "10.0.13",
    });
    const router = await createRouterFor("/", "home");
    const Host = defineComponent({
      components: { NMessageProvider, SettingsView },
      template: "<NMessageProvider><SettingsView /></NMessageProvider>",
    });
    const wrapper = mount(Host, {
      global: {
        plugins: [pinia, router],
        stubs: settingsStubs,
      },
    });
    const inputs = wrapper.findAll("input");
    await inputs[0]!.setValue("old-secret");
    await inputs[1]!.setValue("new-secret-value");
    await inputs[2]!.setValue("new-secret-value");

    clearSensitiveState("manual_lock");
    await nextTick();

    expect(
      inputs.map((input) => (input.element as HTMLInputElement).value),
    ).toEqual(["", "", ""]);
    wrapper.unmount();
  });

  it("clears one-time MCP token state on session clear", async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    const settings = useSettingsStore();
    vi.spyOn(settings, "loadMcpStatus").mockResolvedValue();
    vi.spyOn(settings, "rotateMcpToken").mockResolvedValue({
      token: "raw-one-time-token",
      version: 1,
      createdAtUtc: "2026-11-02T00:00:00Z",
      rotatedAtUtc: "2026-11-02T00:00:00Z",
    });
    const Host = defineComponent({
      components: { NMessageProvider, McpTokenPanel },
      template: "<NMessageProvider><McpTokenPanel /></NMessageProvider>",
    });
    const wrapper = mount(Host, {
      global: {
        plugins: [pinia],
        stubs: tokenStubs,
      },
    });

    wrapper.findComponent(NPopconfirm).vm.$emit("positive-click");
    await flushPromises();
    expect(wrapper.get("[data-test='raw-token']").text()).toBe(
      "raw-one-time-token",
    );

    clearSensitiveState("manual_lock");
    await nextTick();

    expect(wrapper.get("[data-test='raw-token']").text()).toBe("");
    wrapper.unmount();
  });
});
