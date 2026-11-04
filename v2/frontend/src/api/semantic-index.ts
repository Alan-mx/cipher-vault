import type { SemanticIndexStatus } from "../types/api";
import { apiClient } from "./client";

const path = "/api/semantic-index";

export const semanticIndexApi = {
  status: (signal?: AbortSignal) =>
    apiClient.request<SemanticIndexStatus>(`${path}/status`, {
      ...(signal ? { signal } : {}),
    }),

  grantRemoteConsent: (
    profileId: string,
    endpointFingerprint: string,
    idempotencyKey: string,
  ) =>
    apiClient.request<SemanticIndexStatus>(`${path}/remote-consent/grant`, {
      method: "POST",
      csrf: true,
      idempotencyKey,
      body: { profileId, endpointFingerprint },
    }),

  revokeRemoteConsent: (
    profileId: string,
    endpointFingerprint: string,
    idempotencyKey: string,
  ) =>
    apiClient.request<SemanticIndexStatus>(`${path}/remote-consent/revoke`, {
      method: "POST",
      csrf: true,
      idempotencyKey,
      body: { profileId, endpointFingerprint },
    }),

  rebuild: (profileId: string, idempotencyKey: string) =>
    apiClient.request<SemanticIndexStatus>(`${path}/rebuild`, {
      method: "POST",
      csrf: true,
      idempotencyKey,
      body: { profileId },
    }),
};
