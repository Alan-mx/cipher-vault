import type { AntiforgeryTokenResponse, VaultStatus } from "../types/api";
import { apiClient } from "./client";

export const vaultApi = {
  status: () =>
    apiClient.request<VaultStatus>("/api/vault/status", {
      auth: "none",
    }),

  setup: (password: string, riskAccepted: boolean) =>
    apiClient.request<VaultStatus>("/api/vault/setup", {
      method: "POST",
      auth: "none",
      body: { password, riskAccepted },
    }),

  unlock: (password: string) =>
    apiClient.request<VaultStatus>("/api/vault/unlock", {
      method: "POST",
      auth: "none",
      body: { password },
    }),

  antiforgery: () =>
    apiClient.request<AntiforgeryTokenResponse>("/api/vault/antiforgery"),

  lock: () =>
    apiClient.request<VaultStatus>("/api/vault/lock", {
      method: "POST",
      csrf: true,
    }),

  touchActivity: () =>
    apiClient.requestVoid("/api/vault/activity", {
      method: "POST",
      csrf: true,
    }),

  changePassword: (oldPassword: string, newPassword: string) =>
    apiClient.request<VaultStatus>("/api/vault/change-password", {
      method: "POST",
      csrf: true,
      body: { oldPassword, newPassword },
    }),
};
