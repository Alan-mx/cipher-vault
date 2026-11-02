import { afterEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { vaultApi } from "../../src/api";
import {
  clearSensitiveState,
  getSessionGeneration,
  resetSessionLifecycleForTest,
} from "../../src/session/lifecycle";
import {
  resetCsrfRequestForTest,
  useSessionStore,
} from "../../src/stores/session";
import { ApiClientError } from "../../src/types/domain";

interface Deferred<T> {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason?: unknown) => void;
}

const deferred = <T>(): Deferred<T> => {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
};

describe("session CSRF generation isolation", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    resetSessionLifecycleForTest();
    resetCsrfRequestForTest();
  });

  it("does not let an old forced refresh overwrite a newer session token", async () => {
    const first = deferred<{ token: string }>();
    const second = deferred<{ token: string }>();
    const repaired = deferred<{ token: string }>();
    const antiforgery = vi
      .spyOn(vaultApi, "antiforgery")
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise)
      .mockReturnValueOnce(repaired.promise);
    setActivePinia(createPinia());
    const store = useSessionStore();
    store.updateStatus(unlockedStatus());
    const firstGeneration = getSessionGeneration();
    const firstRequest = store.refreshCsrfToken(firstGeneration);
    await Promise.resolve();

    clearSensitiveState("manual_lock");
    store.syncGeneration();
    const secondGeneration = getSessionGeneration();
    const secondRequest = store.refreshCsrfToken(secondGeneration);
    await Promise.resolve();

    second.resolve({ token: "B" });
    await expect(secondRequest).resolves.toBe("B");
    expect(store.csrfToken).toBe("B");

    first.resolve({ token: "A" });
    await expect(firstRequest).resolves.toBe("A");
    await Promise.resolve();
    expect(antiforgery).toHaveBeenCalledTimes(3);
    repaired.resolve({ token: "B-repaired" });
    await Promise.resolve();
    await Promise.resolve();
    expect(store.csrfToken).toBe("B-repaired");
  });

  it("does not let an old finally clear a newer csrf request", async () => {
    const first = deferred<{ token: string }>();
    const second = deferred<{ token: string }>();
    const antiforgery = vi
      .spyOn(vaultApi, "antiforgery")
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise);
    setActivePinia(createPinia());
    const store = useSessionStore();
    store.updateStatus(unlockedStatus());
    const oldGeneration = getSessionGeneration();
    const oldRequest = store.ensureCsrf(oldGeneration);
    await Promise.resolve();

    clearSensitiveState("manual_lock");
    store.syncGeneration();
    const newGeneration = getSessionGeneration();
    const newRequest = store.ensureCsrf(newGeneration);
    await Promise.resolve();
    first.resolve({ token: "A" });
    await oldRequest;

    const sharedNewRequest = store.ensureCsrf(newGeneration);
    expect(antiforgery).toHaveBeenCalledTimes(2);
    second.resolve({ token: "B" });
    await expect(Promise.all([newRequest, sharedNewRequest])).resolves.toEqual([
      "B",
      "B",
    ]);
    expect(store.csrfToken).toBe("B");
  });

  it("shares one forced refresh within the same generation", async () => {
    const request = deferred<{ token: string }>();
    const antiforgery = vi
      .spyOn(vaultApi, "antiforgery")
      .mockReturnValue(request.promise);
    setActivePinia(createPinia());
    const store = useSessionStore();

    const first = store.refreshCsrfToken(getSessionGeneration());
    const second = store.refreshCsrfToken(getSessionGeneration());
    request.resolve({ token: "shared" });

    await expect(Promise.all([first, second])).resolves.toEqual([
      "shared",
      "shared",
    ]);
    expect(antiforgery).toHaveBeenCalledOnce();
  });

  it("clears the cached token before a forced refresh", async () => {
    const request = deferred<{ token: string }>();
    vi.spyOn(vaultApi, "antiforgery").mockReturnValue(request.promise);
    setActivePinia(createPinia());
    const store = useSessionStore();
    store.csrfToken = "old";

    const refresh = store.refreshCsrfToken(getSessionGeneration());
    expect(store.csrfToken).toBeNull();
    request.resolve({ token: "new" });

    await expect(refresh).resolves.toBe("new");
    expect(store.csrfToken).toBe("new");
  });

  it("conditionally clears only the matching generation", () => {
    setActivePinia(createPinia());
    const store = useSessionStore();
    store.syncGeneration();
    const generation = getSessionGeneration();
    const error = new ApiClientError(423, {
      code: "vault_locked",
      message: "Vault is locked.",
    });

    expect(store.handleSessionInvalid(error, generation - 1)).toBe(false);
    expect(store.clearReason).toBeNull();
    expect(store.handleSessionInvalid(error, generation)).toBe(true);
    expect(store.clearReason).toBe("session_expired");
  });

  const unlockedStatus = () => ({
    state: "unlocked" as const,
    isInitialized: true,
    isUnlocked: true,
    idleTimeoutMinutes: 10,
    idleRemainingSeconds: 600,
    lockReason: "none" as const,
  });
});
