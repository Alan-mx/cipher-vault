import { flushPromises, mount } from "@vue/test-utils";
import { createPinia } from "pinia";
import { createMemoryHistory, createRouter } from "vue-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import { vaultApi } from "../../src/api";
import IdleWarningDialog from "../../src/components/lifecycle/IdleWarningDialog.vue";
import { resetSessionLifecycleForTest } from "../../src/session/lifecycle";
import { useSessionStore } from "../../src/stores/session";

const unlockedStatus = {
  state: "unlocked" as const,
  isInitialized: true,
  isUnlocked: true,
  idleTimeoutMinutes: 10,
  idleRemainingSeconds: 600,
  lockReason: "none" as const,
};

const lockedStatus = {
  state: "locked" as const,
  isInitialized: true,
  isUnlocked: false,
  idleTimeoutMinutes: 10,
  idleRemainingSeconds: null,
  lockReason: "user_requested" as const,
};

describe("IdleWarningDialog", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    resetSessionLifecycleForTest();
  });

  it("locks the server before navigating to unlock", async () => {
    const pinia = createPinia();
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        {
          path: "/settings",
          name: "settings",
          component: { template: "" },
        },
        {
          path: "/unlock",
          name: "unlock",
          component: { template: "" },
        },
      ],
    });
    await router.push("/settings");
    await router.isReady();

    const session = useSessionStore(pinia);
    session.updateStatus(unlockedStatus);
    session.monotonicNow = performance.now();
    session.idleDeadlineMonotonic = performance.now() + 1_000;
    const lock = vi.spyOn(vaultApi, "lock").mockResolvedValue(lockedStatus);

    const wrapper = mount(IdleWarningDialog, {
      attachTo: document.body,
      global: {
        plugins: [pinia, router],
      },
    });

    await flushPromises();
    const lockButton = document.querySelector<HTMLButtonElement>(
      ".idle-actions button",
    );
    expect(lockButton).not.toBeNull();
    lockButton!.click();
    await vi.waitFor(() => {
      expect(lock).toHaveBeenCalledOnce();
      expect(router.currentRoute.value.name).toBe("unlock");
    });
    wrapper.unmount();
  });
});
