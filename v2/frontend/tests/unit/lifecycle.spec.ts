import { afterEach, describe, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createMemoryHistory, createRouter } from "vue-router";
import {
  clearSensitiveState,
  getSessionAbortSignal,
  getSessionGeneration,
  registerDirtyStateProvider,
  registerSensitiveStore,
  resetSessionLifecycleForTest,
} from "../../src/session/lifecycle";
import { installSessionLifecycle } from "../../src/session/install";
import { idleWarningThresholdSeconds } from "../../src/stores/session";
import { useSessionStore } from "../../src/stores/session";

describe("session lifecycle", () => {
  let disposeLifecycle: (() => void) | undefined;

  afterEach(() => {
    disposeLifecycle?.();
    disposeLifecycle = undefined;
    vi.restoreAllMocks();
    resetSessionLifecycleForTest();
  });

  it("clears every registered sensitive store and rotates the abort signal", () => {
    const resetA = vi.fn();
    const resetB = vi.fn();
    registerSensitiveStore({ resetSensitive: resetA });
    registerSensitiveStore({ resetSensitive: resetB });
    const originalSignal = getSessionAbortSignal();

    const generation = clearSensitiveState("manual_lock");

    expect(generation).toBe(1);
    expect(resetA).toHaveBeenCalledOnce();
    expect(resetB).toHaveBeenCalledOnce();
    expect(getSessionGeneration()).toBe(1);
    expect(originalSignal.aborted).toBe(true);
    expect(getSessionAbortSignal().aborted).toBe(false);
  });

  it("rotates generation before aborting the previous session controller", () => {
    const originalSignal = getSessionAbortSignal();
    let generationWhenAborted = -1;
    originalSignal.addEventListener("abort", () => {
      generationWhenAborted = getSessionGeneration();
    });

    const generation = clearSensitiveState("manual_lock");

    expect(generation).toBe(1);
    expect(generationWhenAborted).toBe(generation);
  });

  it("runs session local cleanup after abort and before other stores", () => {
    const order: string[] = [];
    const originalSignal = getSessionAbortSignal();
    originalSignal.addEventListener("abort", () => {
      order.push("abort");
    });
    registerSensitiveStore({
      resetSensitive: () => order.push("sensitive"),
    });
    window.addEventListener("cv:session-cleared", () => order.push("event"), {
      once: true,
    });

    clearSensitiveState("pagehide", () => {
      expect(originalSignal.aborted).toBe(true);
      expect(getSessionAbortSignal().aborted).toBe(false);
      order.push("local");
    });

    expect(order).toEqual(["abort", "local", "sensitive", "event"]);
  });

  it("uses the smaller of 60 seconds and 20 percent of the idle window", () => {
    expect(idleWarningThresholdSeconds(1)).toBe(12);
    expect(idleWarningThresholdSeconds(10)).toBe(60);
  });

  it("confirms before leaving a dirty editor for an unknown route", async () => {
    const pinia = createPinia();
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        {
          path: "/documents",
          name: "documents",
          component: { template: "" },
          meta: { layout: "workspace", vault: "unlocked" },
        },
        {
          path: "/:pathMatch(.*)*",
          name: "not-found",
          component: { template: "" },
        },
      ],
    });
    const session = useSessionStore(pinia);
    session.updateStatus({
      state: "unlocked",
      isInitialized: true,
      isUnlocked: true,
      idleTimeoutMinutes: 10,
      idleRemainingSeconds: 600,
      lockReason: "none",
    });
    await router.push("/documents");
    await router.isReady();

    registerDirtyStateProvider(() => true);
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    disposeLifecycle = installSessionLifecycle(router, pinia);

    await router.push("/unknown");

    expect(confirm).toHaveBeenCalledOnce();
    expect(router.currentRoute.value.name).toBe("documents");
  });
});
