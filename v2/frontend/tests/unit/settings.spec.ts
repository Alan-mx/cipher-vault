import { createPinia, setActivePinia } from "pinia";
import { afterEach, describe, expect, it, vi } from "vitest";
import { versionApi } from "../../src/api";
import { useSettingsStore } from "../../src/stores/settings";
import { resetSessionLifecycleForTest } from "../../src/session/lifecycle";

describe("settings store", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    resetSessionLifecycleForTest();
  });

  it("clears token status on lock cleanup", () => {
    setActivePinia(createPinia());
    const settings = useSettingsStore();
    settings.mcpStatus = {
      configured: true,
      version: 1,
      createdAtUtc: "2026-10-31T00:00:00Z",
      rotatedAtUtc: "2026-10-31T00:00:00Z",
    };

    settings.resetSensitive();

    expect(settings.mcpStatus).toBeNull();
  });

  it("loads version metadata and clears it on lock cleanup", async () => {
    setActivePinia(createPinia());
    const settings = useSettingsStore();
    vi.spyOn(versionApi, "get").mockResolvedValue({
      productVersion: "2.0.0-rc.1",
      commit: "0123456789abcdef0123456789abcdef01234567",
      rid: "win-x64",
      runtimeVersion: "10.0.13",
      aspNetCoreVersion: "10.0.13",
    });

    await settings.loadVersion();

    expect(settings.version?.productVersion).toBe("2.0.0-rc.1");
    expect(settings.version?.rid).toBe("win-x64");

    settings.resetSensitive();

    expect(settings.version).toBeNull();
  });

  it("keeps a safe error when version metadata is unavailable", async () => {
    setActivePinia(createPinia());
    const settings = useSettingsStore();
    vi.spyOn(versionApi, "get").mockRejectedValue(new Error("offline"));

    await settings.loadVersion();

    expect(settings.version).toBeNull();
    expect(settings.versionError?.code).toBe("network_error");
    expect(settings.loadingVersion).toBe(false);
  });
});
