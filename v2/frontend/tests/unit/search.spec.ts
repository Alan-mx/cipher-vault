import { createPinia, setActivePinia } from "pinia";
import { afterEach, describe, expect, it, vi } from "vitest";
import { knowledgeApi } from "../../src/api";
import { useSearchStore } from "../../src/stores/search";
import { resetSessionLifecycleForTest } from "../../src/session/lifecycle";

describe("search store", () => {
  afterEach(() => {
    resetSessionLifecycleForTest();
  });

  it("clears all search memory on sensitive reset", async () => {
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
    expect(search.filters.propertyMatch).toBe("all");
    expect(search.filters.propertyFilters).toEqual([]);
  });

  it("allows a property-only structured query", async () => {
    setActivePinia(createPinia());
    const search = useSearchStore();
    search.addPropertyFilter();
    const filter = search.filters.propertyFilters[0]!;
    search.updatePropertyFilter(filter.id, {
      field: "source.kind",
      operator: "eq",
      valueType: "string",
      value: "project",
    });
    search.setPropertyMatch("any");
    const request = vi.spyOn(knowledgeApi, "search").mockResolvedValue({
      hits: [],
      coverage: {
        totalActiveDocuments: 0,
        scannedDocuments: 0,
        matchedDocuments: 0,
        returnedDocuments: 0,
        truncated: false,
      },
      nextCursor: null,
    });

    expect(search.canSearch).toBe(true);
    await search.search();

    expect(request).toHaveBeenCalledOnce();
    expect(request.mock.calls[0]![0]).toMatchObject({
      query: "",
      propertyMatch: "any",
      propertyFilters: [
        {
          field: "source.kind",
          operator: "eq",
          value: "project",
        },
      ],
    });
  });

  it("keeps an empty query when another filter is active", () => {
    setActivePinia(createPinia());
    const search = useSearchStore();
    search.setFilters({ tags: ["Work"] });
    search.query = "needle";

    search.setQuery("   ");

    expect(search.query).toBe("   ");
    expect(search.canSearch).toBe(true);
  });
});
