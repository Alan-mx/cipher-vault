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

let csrfRequest: Promise<string> | null = null;

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

  const refreshStatus = async (): Promise<VaultStatus> => {
    const wasUnlocked = isUnlocked.value;
    const next = await vaultApi.status();
    updateStatus(next);
    if (!next.isUnlocked && wasUnlocked) {
      clearSensitive("session_expired");
    }
    return next;
  };

  const bootstrap = async (): Promise<VaultStatus> => {
    try {
      return await refreshStatus();
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

  const ensureCsrf = async (): Promise<string> => {
    if (csrfToken.value) {
      return csrfToken.value;
    }
    if (csrfRequest) {
      return await csrfRequest;
    }

    csrfRequest = (async () => {
      const response = await vaultApi.antiforgery();
      csrfToken.value = response.token;
      return response.token;
    })();
    try {
      return await csrfRequest;
    } finally {
      csrfRequest = null;
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
    clearReason.value = reason;
    lastError.value = error;
    csrfToken.value = null;
    sessionGeneration.value = clearSensitiveState(reason);
    idleDeadlineMonotonic.value = null;
  };

  const handleSessionInvalid = (error: ApiClientError): void => {
    clearSensitive(
      error.code === "vault_lock_failed" ? "lock_failed" : "session_expired",
      error,
    );
    if (status.value) {
      status.value = {
        ...status.value,
        state: error.code === "vault_lock_failed" ? "lock_failed" : "locked",
        isUnlocked: false,
      };
    }
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
