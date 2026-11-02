export type SessionClearReason =
  | "manual_lock"
  | "idle_lock"
  | "session_expired"
  | "lock_failed"
  | "pagehide"
  | "bfcache_revalidate"
  | "password_changed";

export interface SensitiveStore {
  resetSensitive: () => void;
}

const resetHandlers = new Set<() => void>();
const dirtyProviders = new Set<() => boolean>();
let generation = 0;
let requestController = new AbortController();
let clearing = false;

export const getSessionGeneration = (): number => generation;

export const getSessionAbortSignal = (): AbortSignal =>
  requestController.signal;

export const registerSensitiveStore = (store: SensitiveStore): (() => void) => {
  const handler = (): void => {
    store.resetSensitive();
  };
  resetHandlers.add(handler);
  return () => {
    resetHandlers.delete(handler);
  };
};

export const registerDirtyStateProvider = (
  provider: () => boolean,
): (() => void) => {
  dirtyProviders.add(provider);
  return () => {
    dirtyProviders.delete(provider);
  };
};

export const hasDirtySensitiveState = (): boolean =>
  Array.from(dirtyProviders).some((provider) => provider());

export const clearSensitiveState = (
  reason: SessionClearReason,
  localCleanup?: (reason: SessionClearReason) => void,
): number => {
  if (clearing) {
    return generation;
  }

  clearing = true;
  try {
    generation++;
    requestController.abort();
    requestController = new AbortController();
    localCleanup?.(reason);
    for (const reset of Array.from(resetHandlers)) {
      try {
        reset();
      } catch {
        // One store must not prevent the remaining sensitive stores from clearing.
      }
    }
    window.dispatchEvent(
      new CustomEvent<SessionClearReason>("cv:session-cleared", {
        detail: reason,
      }),
    );
    return generation;
  } finally {
    clearing = false;
  }
};

export const resetSessionLifecycleForTest = (): void => {
  generation = 0;
  requestController.abort();
  requestController = new AbortController();
  resetHandlers.clear();
  dirtyProviders.clear();
  clearing = false;
};
