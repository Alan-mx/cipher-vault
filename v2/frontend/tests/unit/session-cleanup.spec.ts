import { afterEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia, type Pinia } from "pinia";
import { createMemoryHistory, createRouter, type Router } from "vue-router";
import { vaultApi } from "../../src/api";
import {
  getSessionGeneration,
  resetSessionLifecycleForTest,
} from "../../src/session/lifecycle";
import { installSessionLifecycle } from "../../src/session/install";
import {
  resetCsrfRequestForTest,
  useSessionStore,
} from "../../src/stores/session";

describe("session page lifecycle cleanup", () => {
  let disposeLifecycle: (() => void) | undefined;

  afterEach(() => {
    disposeLifecycle?.();
    disposeLifecycle = undefined;
    vi.restoreAllMocks();
    resetSessionLifecycleForTest();
    resetCsrfRequestForTest();
  });

  it("clears all local session state through the shared entrypoint", () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    const store = useSessionStore(pinia);
    store.updateStatus(unlockedStatus());
    store.csrfToken = "csrf";
    store.activityLastRequestMonotonic = 42;
    store.idleDeadlineMonotonic = 99;
    store.bootstrapped = true;
    const generation = getSessionGeneration();

    store.clearSensitive("pagehide");

    expect(getSessionGeneration()).toBe(generation + 1);
    expect(store.status).toBeNull();
    expect(store.csrfToken).toBeNull();
    expect(store.activityLastRequestMonotonic).toBeNull();
    expect(store.idleDeadlineMonotonic).toBeNull();
    expect(store.bootstrapped).toBe(false);
    expect(store.revalidating).toBe(false);
    expect(store.lastError).toBeNull();
    expect(store.clearReason).toBe("pagehide");
  });

  it("does not send Lock on pagehide and clears the store immediately", async () => {
    const { pinia, router } = await createInstalledSession();
    disposeLifecycle = installSessionLifecycle(router, pinia);
    const store = useSessionStore(pinia);
    store.updateStatus(unlockedStatus());
    store.csrfToken = "csrf";
    const lock = vi.spyOn(vaultApi, "lock");

    window.dispatchEvent(
      new PageTransitionEvent("pagehide", { persisted: false }),
    );

    expect(lock).not.toHaveBeenCalled();
    expect(store.status).toBeNull();
    expect(store.csrfToken).toBeNull();
    expect(store.bootstrapped).toBe(false);
    expect(store.clearReason).toBe("pagehide");
  });

  it("revalidates bfcache without Lock and does not show stale state", async () => {
    const { pinia, router } = await createInstalledSession();
    disposeLifecycle = installSessionLifecycle(router, pinia);
    const store = useSessionStore(pinia);
    store.updateStatus(unlockedStatus());
    store.csrfToken = "csrf";
    const status = vi
      .spyOn(vaultApi, "status")
      .mockResolvedValue(unlockedStatus());
    const lock = vi.spyOn(vaultApi, "lock");

    window.dispatchEvent(
      new PageTransitionEvent("pageshow", { persisted: true }),
    );
    expect(store.revalidating).toBe(true);
    expect(store.csrfToken).toBeNull();
    await Promise.resolve();
    await Promise.resolve();

    expect(status).toHaveBeenCalledOnce();
    expect(lock).not.toHaveBeenCalled();
    expect(store.status?.state).toBe("unlocked");
    await vi.waitFor(() => {
      expect(store.revalidating).toBe(false);
    });
    expect(store.csrfToken).toBeNull();
  });

  it("drops a stale bootstrap result after a newer revalidation starts", async () => {
    const { pinia, router } = await createInstalledSession();
    disposeLifecycle = installSessionLifecycle(router, pinia);
    const store = useSessionStore(pinia);
    const first = deferred<ReturnType<typeof unlockedStatus>>();
    const second = deferred<ReturnType<typeof unlockedStatus>>();
    vi.spyOn(vaultApi, "status")
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise);

    window.dispatchEvent(
      new PageTransitionEvent("pageshow", { persisted: true }),
    );
    window.dispatchEvent(
      new PageTransitionEvent("pageshow", { persisted: true }),
    );

    second.resolve(unlockedStatus());
    await Promise.resolve();
    await Promise.resolve();
    expect(store.status?.state).toBe("unlocked");

    first.resolve({ ...unlockedStatus(), state: "locked", isUnlocked: false });
    await Promise.resolve();
    await Promise.resolve();
    expect(store.status?.state).toBe("unlocked");
    await vi.waitFor(() => {
      expect(store.revalidating).toBe(false);
    });
  });
});

const createInstalledSession = async (): Promise<{
  pinia: Pinia;
  router: Router;
}> => {
  const pinia = createPinia();
  setActivePinia(pinia);
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      {
        path: "/documents",
        name: "documents",
        component: { template: "" },
        meta: { vault: "unlocked" },
      },
      {
        path: "/unlock",
        name: "unlock",
        component: { template: "" },
      },
    ],
  });
  await router.push("/documents");
  await router.isReady();
  return { pinia, router };
};

const unlockedStatus = () => ({
  state: "unlocked" as const,
  isInitialized: true,
  isUnlocked: true,
  idleTimeoutMinutes: 10,
  idleRemainingSeconds: 600,
  lockReason: "none" as const,
});

const deferred = <T>() => {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
};
