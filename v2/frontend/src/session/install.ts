import type { Pinia } from "pinia";
import type { Router } from "vue-router";
import { configureApiClient } from "../api";
import { useSessionStore } from "../stores/session";
import {
  clearSensitiveState,
  getSessionAbortSignal,
  getSessionGeneration,
  hasDirtySensitiveState,
} from "./lifecycle";

export const installSessionLifecycle = (
  router: Router,
  pinia: Pinia,
): (() => void) => {
  const session = useSessionStore(pinia);
  configureApiClient({
    getCsrfToken: () => session.csrfToken,
    refreshCsrfToken: () => session.ensureCsrf(),
    currentSessionGeneration: getSessionGeneration,
    currentAbortSignal: getSessionAbortSignal,
    onSessionInvalid: (error) => {
      session.handleSessionInvalid(error);
      if (router.currentRoute.value.name !== "unlock") {
        void router.replace({ name: "unlock" });
      }
    },
  });

  let pollTimer: number | null = null;
  let tickTimer: number | null = null;
  let lastActivityRequest = 0;

  const syncRoute = async (): Promise<void> => {
    const current = router.currentRoute.value;
    if (current.meta.vault === "unlocked" && !session.isUnlocked) {
      await router.replace({ name: "unlock" });
    }
  };

  const refresh = async (): Promise<void> => {
    try {
      await session.refreshStatus();
      await syncRoute();
    } catch {
      // API client already handles authentication and lock transitions.
    }
  };

  const noteActivity = (): void => {
    if (!session.isUnlocked) {
      return;
    }
    const now = performance.now();
    if (now - lastActivityRequest < 30_000) {
      return;
    }
    lastActivityRequest = now;
    void session.touchActivity().catch(() => undefined);
  };

  const handleSessionCleared = (event: Event): void => {
    session.syncGeneration();
    const reason = (event as CustomEvent<string>).detail;
    if (
      reason !== "pagehide" &&
      router.currentRoute.value.name !== "unlock" &&
      router.currentRoute.value.name !== "lock-failed"
    ) {
      void router.replace({ name: "unlock" });
    }
  };

  const handleBeforeUnload = (event: BeforeUnloadEvent): void => {
    if (!hasDirtySensitiveState()) {
      return;
    }
    event.preventDefault();
    event.returnValue = "";
  };

  const handlePageHide = (): void => {
    clearSensitiveState("pagehide");
  };

  const handlePageShow = (event: PageTransitionEvent): void => {
    if (!event.persisted) {
      return;
    }
    session.revalidating = true;
    clearSensitiveState("bfcache_revalidate");
    void session
      .bootstrap()
      .then(async () => {
        await syncRoute();
      })
      .finally(() => {
        session.revalidating = false;
      });
  };

  router.beforeEach(async (to, from) => {
    if (!session.bootstrapped || !session.status) {
      await session.bootstrap();
    }
    if (
      from.meta.layout === "workspace" &&
      to.fullPath !== from.fullPath &&
      !session.canLeaveDirtyEditor()
    ) {
      return false;
    }
    if (to.name === "not-found") {
      return true;
    }

    const state = session.status?.state;
    if (state === "uninitialized") {
      return to.name === "setup" ? true : { name: "setup" };
    }
    if (state === "locked") {
      return to.name === "unlock" ? true : { name: "unlock" };
    }
    if (state === "lock_failed") {
      return to.name === "lock-failed" ? true : { name: "lock-failed" };
    }
    if (state !== "unlocked") {
      return to.name === "boot" ? true : { name: "boot" };
    }
    if (to.name === "setup" || to.name === "unlock" || to.name === "boot") {
      return { name: "documents" };
    }
    return true;
  });

  window.addEventListener("cv:session-cleared", handleSessionCleared);
  window.addEventListener("beforeunload", handleBeforeUnload);
  window.addEventListener("pagehide", handlePageHide);
  window.addEventListener("pageshow", handlePageShow);
  window.addEventListener("keydown", noteActivity, { capture: true });
  window.addEventListener("pointerdown", noteActivity, { capture: true });
  window.addEventListener("input", noteActivity, { capture: true });

  pollTimer = window.setInterval(() => {
    if (session.isUnlocked) {
      void refresh();
    }
  }, 10_000);
  tickTimer = window.setInterval(() => {
    session.monotonicNow = performance.now();
  }, 1_000);

  return () => {
    if (pollTimer !== null) {
      window.clearInterval(pollTimer);
    }
    if (tickTimer !== null) {
      window.clearInterval(tickTimer);
    }
    window.removeEventListener("cv:session-cleared", handleSessionCleared);
    window.removeEventListener("beforeunload", handleBeforeUnload);
    window.removeEventListener("pagehide", handlePageHide);
    window.removeEventListener("pageshow", handlePageShow);
    window.removeEventListener("keydown", noteActivity, { capture: true });
    window.removeEventListener("pointerdown", noteActivity, { capture: true });
    window.removeEventListener("input", noteActivity, { capture: true });
  };
};
