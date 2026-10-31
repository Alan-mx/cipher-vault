import { createPinia, setActivePinia } from "pinia";
import { afterEach, describe, expect, it } from "vitest";
import { useSearchStore } from "../../src/stores/search";
import { resetSessionLifecycleForTest } from "../../src/session/lifecycle";

describe("search store", () => {
  afterEach(() => {
    resetSessionLifecycleForTest();
  });

  it("requires a non-empty query and clears all search memory", async () => {
    setActivePinia(createPinia());
    const search = useSearchStore();
    search.query = "secret";
    search.setFilters({ tags: ["Work"], orphanOnly: true });

    search.resetSensitive();
    await search.search();

    expect(search.query).toBe("");
    expect(search.filters.tags).toEqual([]);
    expect(search.filters.orphanOnly).toBe(false);
    expect(search.hits).toEqual([]);
    expect(search.coverage).toBeNull();
  });
});
