import { ref } from "vue";
import { defineStore } from "pinia";
import { settingsApi, vaultApi, versionApi } from "../api";
import { registerSensitiveStore } from "../session/lifecycle";
import type {
  McpTokenRotateResponse,
  McpTokenStatus,
  VersionInfo,
  VaultStatus,
} from "../types/api";
import { ApiClientError } from "../types/domain";

export const useSettingsStore = defineStore("settings", () => {
  const mcpStatus = ref<McpTokenStatus | null>(null);
  const loadingMcp = ref(false);
  const changingPassword = ref(false);
  const error = ref<ApiClientError | null>(null);
  const version = ref<VersionInfo | null>(null);
  const loadingVersion = ref(false);
  const versionError = ref<ApiClientError | null>(null);

  const loadVersion = async (): Promise<void> => {
    loadingVersion.value = true;
    versionError.value = null;
    try {
      version.value = await versionApi.get();
    } catch (caught) {
      versionError.value =
        caught instanceof ApiClientError
          ? caught
          : new ApiClientError(0, {
              code: "network_error",
              message: "无法读取版本信息。",
            });
    } finally {
      loadingVersion.value = false;
    }
  };

  const loadMcpStatus = async (): Promise<void> => {
    loadingMcp.value = true;
    error.value = null;
    try {
      mcpStatus.value = await settingsApi.mcpStatus();
    } catch (caught) {
      error.value =
        caught instanceof ApiClientError
          ? caught
          : new ApiClientError(0, {
              code: "network_error",
              message: "无法读取 MCP Token 状态。",
            });
    } finally {
      loadingMcp.value = false;
    }
  };

  const rotateMcpToken = async (): Promise<McpTokenRotateResponse> => {
    const result = await settingsApi.rotateMcpToken();
    mcpStatus.value = {
      configured: true,
      version: result.version,
      createdAtUtc: result.createdAtUtc,
      rotatedAtUtc: result.rotatedAtUtc,
    };
    return result;
  };

  const changePassword = async (
    oldPassword: string,
    newPassword: string,
  ): Promise<VaultStatus> => {
    changingPassword.value = true;
    error.value = null;
    try {
      return await vaultApi.changePassword(oldPassword, newPassword);
    } catch (caught) {
      error.value =
        caught instanceof ApiClientError
          ? caught
          : new ApiClientError(0, {
              code: "network_error",
              message: "修改密码失败。",
            });
      throw caught;
    } finally {
      changingPassword.value = false;
    }
  };

  const resetSensitive = (): void => {
    mcpStatus.value = null;
    loadingMcp.value = false;
    changingPassword.value = false;
    error.value = null;
    version.value = null;
    loadingVersion.value = false;
    versionError.value = null;
  };

  registerSensitiveStore({ resetSensitive });

  return {
    mcpStatus,
    loadingMcp,
    changingPassword,
    error,
    version,
    loadingVersion,
    versionError,
    loadVersion,
    loadMcpStatus,
    rotateMcpToken,
    changePassword,
    resetSensitive,
  };
});
