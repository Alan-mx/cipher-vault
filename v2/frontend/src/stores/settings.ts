import { ref } from "vue";
import { defineStore } from "pinia";
import { settingsApi, vaultApi } from "../api";
import { registerSensitiveStore } from "../session/lifecycle";
import type {
  McpTokenRotateResponse,
  McpTokenStatus,
  VaultStatus,
} from "../types/api";
import { ApiClientError } from "../types/domain";

export const useSettingsStore = defineStore("settings", () => {
  const mcpStatus = ref<McpTokenStatus | null>(null);
  const loadingMcp = ref(false);
  const changingPassword = ref(false);
  const error = ref<ApiClientError | null>(null);

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
  };

  registerSensitiveStore({ resetSensitive });

  return {
    mcpStatus,
    loadingMcp,
    changingPassword,
    error,
    loadMcpStatus,
    rotateMcpToken,
    changePassword,
    resetSensitive,
  };
});
