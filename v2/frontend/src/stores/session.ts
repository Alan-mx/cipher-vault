import { computed, ref } from "vue";
import { defineStore } from "pinia";
import { vaultApi } from "../api";
import { ApiClientError, StaleSessionResponseError } from "../types/domain";
import type { VaultStatus } from "../types/api";
import {
  clearSensitiveState,
  getSessionGeneration,
  hasDirtySensitiveState,
  type SessionClearReason,
} from "../session/lifecycle";

interface CsrfRequest {
  generation: number;
  requestId: number;
  forced: boolean;
  promise: Promise<string>;
}

let csrfRequest: CsrfRequest | null = null;
let nextCsrfRequestId = 0;
let csrfRepair: { generation: number; promise: Promise<void> } | null = null;

export const resetCsrfRequestForTest = (): void => {
  csrfRequest = null;
  nextCsrfRequestId = 0;
  csrfRepair = null;
};

export const idleWarningThresholdSeconds = (
  idleTimeoutMinutes: number,
): number => Math.min(60, idleTimeoutMinutes * 60 * 0.2);

export const useSessionStore = defineStore("session", () => {
  const status = ref<VaultStatus | null>(null);
  const csrfToken = ref<string | null>(null);
  const sessionGeneration = ref(getSessionGeneration());
  const bootstrapped = ref(false);
  const revalidating = ref(false);
  const clearReason = ref<SessionClearReason | null>(null);
  const lastError = ref<ApiClientError | null>(null);
  const idleDeadlineMonotonic = ref<number | null>(null);
  const monotonicNow = ref(performance.now());
  const activityLastRequestMonotonic = ref<number | null>(null);

  const isUnlocked = computed(() => status.value?.state === "unlocked");
  const isLocked = computed(
    () =>
      status.value?.state === "locked" || status.value?.state === "lock_failed",
  );
  const idleRemainingSeconds = computed(() => {
    if (!isUnlocked.value || idleDeadlineMonotonic.value === null) {
      return null;
    }
    return Math.max(
      0,
      Math.ceil((idleDeadlineMonotonic.value - monotonicNow.value) / 1000),
    );
  });
  const idleWarningThreshold = computed(() =>
    status.value
      ? idleWarningThresholdSeconds(status.value.idleTimeoutMinutes)
      : 60,
  );
  const showIdleWarning = computed(
    () =>
      isUnlocked.value &&
      idleRemainingSeconds.value !== null &&
      idleRemainingSeconds.value > 0 &&
      idleRemainingSeconds.value < idleWarningThreshold.value,
  );

  const syncGeneration = (): void => {
    sessionGeneration.value = getSessionGeneration();
  };

  const updateStatus = (next: VaultStatus): void => {
    status.value = next;
    idleDeadlineMonotonic.value =
      next.state === "unlocked" && next.idleRemainingSeconds !== null
        ? performance.now() + next.idleRemainingSeconds * 1000
        : null;
    if (next.state !== "unlocked") {
      csrfToken.value = null;
    }
    bootstrapped.value = true;
  };

  const refreshStatus = async (
    expectedGeneration: number = getSessionGeneration(),
  ): Promise<VaultStatus> => {
    const wasUnlocked = isUnlocked.value;
    const next = await vaultApi.status();
    if (getSessionGeneration() !== expectedGeneration) {
      throw new StaleSessionResponseError();
    }
    updateStatus(next);
    if (!next.isUnlocked && wasUnlocked) {
      clearSensitive("session_expired");
    }
    return next;
  };

  const bootstrap = async (
    expectedGeneration: number = getSessionGeneration(),
  ): Promise<VaultStatus> => {
    try {
      return await refreshStatus(expectedGeneration);
    } catch (error) {
      if (error instanceof StaleSessionResponseError) {
        throw error;
      }
      lastError.value =
        error instanceof ApiClientError
          ? error
          : new ApiClientError(0, {
              code: "network_error",
              message: "Unable to reach CipherVault.Server.",
            });
      throw error;
    }
  };

  const createCsrfRequest = (
    generation: number,
    forced: boolean,
  ): CsrfRequest => {
    const request = {
      generation,
      requestId: ++nextCsrfRequestId,
      forced,
      promise: Promise.resolve(""),
    } as CsrfRequest;
    request.promise = (async () => {
      const response = await vaultApi.antiforgery();
      const matchesRequest =
        getSessionGeneration() === request.generation &&
        csrfRequest === request;
      if (matchesRequest) {
        csrfToken.value = response.token;
      } else {
        scheduleCsrfRepairIfNeeded();
      }
      return response.token;
    })();
    csrfRequest = request;
    return request;
  };

  const scheduleCsrfRepairIfNeeded = (): void => {
    const generation = getSessionGeneration();
    if (
      status.value?.state !== "unlocked" ||
      (csrfRequest && csrfRequest.generation === generation) ||
      csrfRepair?.generation === generation
    ) {
      return;
    }

    const promise = (async () => {
      try {
        await refreshCsrfToken(generation);
      } catch (error) {
        if (!(error instanceof StaleSessionResponseError)) {
          // CSRF recovery remains best effort; the next request will retry.
        }
      }
    })().finally(() => {
      if (csrfRepair?.generation === generation) {
        csrfRepair = null;
      }
    });
    csrfRepair = { generation, promise };
  };

  const ensureCsrf = async (
    expectedGeneration: number = getSessionGeneration(),
  ): Promise<string> => {
    if (getSessionGeneration() !== expectedGeneration) {
      throw new StaleSessionResponseError();
    }
    if (csrfToken.value) {
      return csrfToken.value;
    }
    if (
      csrfRequest &&
      csrfRequest.generation === expectedGeneration &&
      !csrfRequest.forced
    ) {
      return await csrfRequest.promise;
    }

    const request = createCsrfRequest(expectedGeneration, false);
    try {
      return await request.promise;
    } finally {
      if (csrfRequest === request) {
        csrfRequest = null;
      }
    }
  };

  const refreshCsrfToken = async (
    expectedGeneration: number,
  ): Promise<string> => {
    if (getSessionGeneration() !== expectedGeneration) {
      throw new StaleSessionResponseError();
    }
    if (
      csrfRequest &&
      csrfRequest.generation === expectedGeneration &&
      csrfRequest.forced
    ) {
      return await csrfRequest.promise;
    }

    csrfToken.value = null;
    const request = createCsrfRequest(expectedGeneration, true);
    try {
      return await request.promise;
    } finally {
      if (csrfRequest === request) {
        csrfRequest = null;
      }
    }
  };

  const setup = async (
    password: string,
    riskAccepted: boolean,
  ): Promise<VaultStatus> => {
    const next = await vaultApi.setup(password, riskAccepted);
    updateStatus(next);
    return next;
  };

  const unlock = async (password: string): Promise<VaultStatus> => {
    const next = await vaultApi.unlock(password);
    updateStatus(next);
    await ensureCsrf();
    return next;
  };

  const lock = async (): Promise<void> => {
    const next = await vaultApi.lock();
    updateStatus(next);
    clearSensitive("manual_lock");
  };

  const touchActivity = async (): Promise<boolean> => {
    if (!isUnlocked.value) {
      return false;
    }
    const now = performance.now();
    if (
      activityLastRequestMonotonic.value !== null &&
      now - activityLastRequestMonotonic.value < 30_000
    ) {
      return false;
    }
    activityLastRequestMonotonic.value = now;
    try {
      await vaultApi.touchActivity();
      await refreshStatus();
      return true;
    } catch (error) {
      if (
        error instanceof ApiClientError &&
        error.code === "activity_rate_limited"
      ) {
        return false;
      }
      throw error;
    }
  };

  const clearSensitive = (
    reason: SessionClearReason,
    error: ApiClientError | null = null,
  ): void => {
    const clearLocal = (): void => {
      csrfToken.value = null;
      csrfRequest = null;
      csrfRepair = null;
      idleDeadlineMonotonic.value = null;
      activityLastRequestMonotonic.value = null;
      status.value = null;
      bootstrapped.value = false;
      revalidating.value = reason === "bfcache_revalidate";
      clearReason.value = reason;
      lastError.value =
        reason === "session_expired" || reason === "lock_failed" ? error : null;
    };
    sessionGeneration.value = clearSensitiveState(reason, clearLocal);
  };

  const handleSessionInvalid = (
    error: ApiClientError,
    expectedGeneration: number,
  ): boolean => {
    if (
      getSessionGeneration() !== expectedGeneration ||
      sessionGeneration.value !== expectedGeneration
    ) {
      return false;
    }
    clearSensitive(
      error.code === "vault_lock_failed" ? "lock_failed" : "session_expired",
      error,
    );
    return true;
  };

  const canLeaveDirtyEditor = (): boolean => {
    if (!hasDirtySensitiveState()) {
      return true;
    }
    return window.confirm("当前文档尚未保存，确定离开吗？");
  };

  return {
    status,
    csrfToken,
    sessionGeneration,
    bootstrapped,
    revalidating,
    clearReason,
    lastError,
    idleDeadlineMonotonic,
    monotonicNow,
    activityLastRequestMonotonic,
    isUnlocked,
    isLocked,
    idleRemainingSeconds,
    idleWarningThreshold,
    showIdleWarning,
    updateStatus,
    refreshStatus,
    bootstrap,
    ensureCsrf,
    refreshCsrfToken,
    setup,
    unlock,
    lock,
    touchActivity,
    clearSensitive,
    handleSessionInvalid,
    canLeaveDirtyEditor,
    syncGeneration,
  };
});
