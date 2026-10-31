import { createPinia, setActivePinia } from "pinia";
import { afterEach, describe, expect, it } from "vitest";
import { useSettingsStore } from "../../src/stores/settings";
import { resetSessionLifecycleForTest } from "../../src/session/lifecycle";

describe("settings store", () => {
  afterEach(() => {
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
});
