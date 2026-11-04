import { ref } from "vue";
import { defineStore } from "pinia";
import { semanticIndexApi } from "../api";
import { registerSensitiveStore } from "../session/lifecycle";
import type { SemanticIndexStatus } from "../types/api";
import { ApiClientError } from "../types/domain";

export type SemanticIndexAction = "grant" | "revoke" | "rebuild";

export const useSemanticIndexStore = defineStore("semantic-index", () => {
  const status = ref<SemanticIndexStatus | null>(null);
  const loading = ref(false);
  const mutating = ref(false);
  const activeAction = ref<SemanticIndexAction | null>(null);
  const error = ref<ApiClientError | null>(null);
  let controller: AbortController | null = null;

  const load = async (): Promise<void> => {
    controller?.abort();
    controller = new AbortController();
    loading.value = true;
    error.value = null;
    try {
      status.value = await semanticIndexApi.status(controller.signal);
    } catch (caught) {
      if (caught instanceof DOMException && caught.name === "AbortError") {
        return;
      }
      error.value = toApiError(caught, "无法读取语义索引状态。");
    } finally {
      loading.value = false;
      controller = null;
    }
  };

  const grantRemoteConsent = (): Promise<boolean> =>
    runMutation("grant", async () => {
      const profile = requireRemoteProfile();
      return semanticIndexApi.grantRemoteConsent(
        profile.profileId,
        profile.endpointFingerprint,
        crypto.randomUUID(),
      );
    });

  const revokeRemoteConsent = (): Promise<boolean> =>
    runMutation("revoke", async () => {
      const profile = requireRemoteProfile();
      return semanticIndexApi.revokeRemoteConsent(
        profile.profileId,
        profile.endpointFingerprint,
        crypto.randomUUID(),
      );
    });

  const rebuild = (): Promise<boolean> =>
    runMutation("rebuild", async () => {
      const profile = status.value?.profile;
      if (!profile) {
        throw new ApiClientError(0, {
          code: "semantic_profile_unavailable",
          message: "当前没有可重建的语义索引。",
        });
      }
      return semanticIndexApi.rebuild(profile.profileId, crypto.randomUUID());
    });

  const runMutation = async (
    action: SemanticIndexAction,
    request: () => Promise<SemanticIndexStatus>,
  ): Promise<boolean> => {
    if (mutating.value) {
      return false;
    }
    mutating.value = true;
    activeAction.value = action;
    error.value = null;
    try {
      status.value = await request();
      return true;
    } catch (caught) {
      error.value = toApiError(caught, "语义索引操作失败。");
      return false;
    } finally {
      mutating.value = false;
      activeAction.value = null;
    }
  };

  const requireRemoteProfile = () => {
    const profile = status.value?.profile;
    if (!profile?.isRemote) {
      throw new ApiClientError(0, {
        code: "semantic_remote_profile_required",
        message: "当前语义索引不是远程配置。",
      });
    }
    return profile;
  };

  const resetSensitive = (): void => {
    controller?.abort();
    controller = null;
    status.value = null;
    loading.value = false;
    mutating.value = false;
    activeAction.value = null;
    error.value = null;
  };

  registerSensitiveStore({ resetSensitive });

  return {
    status,
    loading,
    mutating,
    activeAction,
    error,
    load,
    grantRemoteConsent,
    revokeRemoteConsent,
    rebuild,
    resetSensitive,
  };
});

const toApiError = (
  caught: unknown,
  fallbackMessage: string,
): ApiClientError =>
  caught instanceof ApiClientError
    ? caught
    : new ApiClientError(0, {
        code: "network_error",
        message: fallbackMessage,
      });
