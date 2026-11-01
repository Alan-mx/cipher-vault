import { createPinia, setActivePinia } from "pinia";
import { afterEach, describe, expect, it, vi } from "vitest";
import { backupApi } from "../../src/api";
import { resetSessionLifecycleForTest } from "../../src/session/lifecycle";
import { useBackupStore } from "../../src/stores/backups";

const item = {
  fileName: "cipher-vault-20261101-000000-deadbeef.cvbackup",
  bytes: 4096,
  modifiedAtUtc: "2026-11-01T00:00:01Z",
  createdAtUtc: "2026-11-01T00:00:00Z",
  formatVersion: 1,
  status: "verified" as const,
  errorCode: null,
};

describe("backup store", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    resetSessionLifecycleForTest();
  });

  it("loads safe backup metadata and clears it on lock cleanup", async () => {
    setActivePinia(createPinia());
    const store = useBackupStore();
    vi.spyOn(backupApi, "list").mockResolvedValue({
      items: [item],
      nextCursor: "next",
    });

    await store.load();

    expect(store.items).toEqual([item]);
    expect(store.nextCursor).toBe("next");

    store.resetSensitive();

    expect(store.items).toEqual([]);
    expect(store.nextCursor).toBeNull();
  });

  it("reloads the first page after creating a backup", async () => {
    setActivePinia(createPinia());
    const store = useBackupStore();
    const list = vi
      .spyOn(backupApi, "list")
      .mockResolvedValueOnce({ items: [], nextCursor: null })
      .mockResolvedValueOnce({ items: [item], nextCursor: null });
    vi.spyOn(backupApi, "create").mockResolvedValue({ backup: item });

    await store.load();
    const created = await store.create();

    expect(created).toEqual(item);
    expect(list).toHaveBeenCalledTimes(2);
    expect(store.items).toEqual([item]);
  });

  it("builds an encoded same-origin download URL", () => {
    expect(backupApi.downloadUrl(item.fileName)).toBe(
      `/api/backups/${encodeURIComponent(item.fileName)}/download`,
    );
  });
});
