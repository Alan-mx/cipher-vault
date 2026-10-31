import { createPinia, setActivePinia } from "pinia";
import { afterEach, describe, expect, it } from "vitest";
import { useImportStore } from "../../src/stores/import";
import { resetSessionLifecycleForTest } from "../../src/session/lifecycle";

describe("import store", () => {
  afterEach(() => {
    resetSessionLifecycleForTest();
  });

  it("keeps source path and preview token in memory and clears them on lock", () => {
    setActivePinia(createPinia());
    const imports = useImportStore();
    imports.sourceRoot = "D:\\Notes";
    imports.preview = {
      token: "preview-token",
      expiresAt: "2026-10-31T00:10:00Z",
      readyCount: 0,
      skippedCount: 0,
      rejectedCount: 0,
      items: [],
    };

    imports.resetSensitive();

    expect(imports.sourceRoot).toBe("");
    expect(imports.preview).toBeNull();
    expect(imports.items).toEqual([]);
  });
});
