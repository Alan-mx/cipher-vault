import { createPinia, setActivePinia } from "pinia";
import { afterEach, describe, expect, it, vi } from "vitest";
import { semanticIndexApi } from "../../src/api";
import { useSemanticIndexStore } from "../../src/stores/semantic-index";
import { resetSessionLifecycleForTest } from "../../src/session/lifecycle";
import type { SemanticIndexStatus } from "../../src/types/api";

const remoteStatus: SemanticIndexStatus = {
  state: "disabled",
  endpointDisplay: "https://embedding.example.com/v1",
  remoteDeploymentAllowed: true,
  remoteConsentRequired: true,
  remoteConsentGranted: false,
  consentGrantedAt: null,
  consentRevokedAt: null,
  documentCount: 0,
  chunkCount: 0,
  pendingJobCount: 0,
  failedJobCount: 0,
  lastErrorCode: null,
  lastUpdatedAt: "2026-11-05T00:00:00Z",
  profile: {
    profileId: "a".repeat(64),
    providerId: "openai-compatible",
    modelId: "remote-embed",
    dimension: 768,
    maxInputTokens: 8192,
    endpointFingerprint: "b".repeat(64),
    isRemote: true,
    profileVersion: 1,
    chunkerVersion: 1,
    status: "disabled",
    lastErrorCode: null,
    createdAt: "2026-11-05T00:00:00Z",
    updatedAt: "2026-11-05T00:00:00Z",
    readyAt: null,
  },
};

describe("semantic index store", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    resetSessionLifecycleForTest();
  });

  it("loads status and uses a fresh idempotency key for consent", async () => {
    setActivePinia(createPinia());
    const store = useSemanticIndexStore();
    vi.spyOn(semanticIndexApi, "status").mockResolvedValue(remoteStatus);
    const grant = vi
      .spyOn(semanticIndexApi, "grantRemoteConsent")
      .mockResolvedValue({
        ...remoteStatus,
        state: "building",
        remoteConsentGranted: true,
      });

    await store.load();
    const changed = await store.grantRemoteConsent();

    expect(changed).toBe(true);
    expect(grant).toHaveBeenCalledOnce();
    expect(grant.mock.calls[0]![0]).toBe(remoteStatus.profile!.profileId);
    expect(grant.mock.calls[0]![1]).toBe(
      remoteStatus.profile!.endpointFingerprint,
    );
    expect(grant.mock.calls[0]![2]).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
    );
    expect(store.status?.remoteConsentGranted).toBe(true);
  });

  it("clears semantic status on session cleanup", async () => {
    setActivePinia(createPinia());
    const store = useSemanticIndexStore();
    vi.spyOn(semanticIndexApi, "status").mockResolvedValue(remoteStatus);

    await store.load();
    expect(store.status).not.toBeNull();

    store.resetSensitive();

    expect(store.status).toBeNull();
    expect(store.error).toBeNull();
    expect(store.mutating).toBe(false);
  });
});
