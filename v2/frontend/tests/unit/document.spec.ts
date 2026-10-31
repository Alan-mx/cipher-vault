import { describe, expect, it } from "vitest";
import { isGuid } from "../../src/utils/guid";

describe("document route validation", () => {
  it("accepts non-empty versioned GUIDs only", () => {
    expect(isGuid("00000000-0000-4000-8000-000000000001")).toBe(true);
    expect(isGuid("not-a-guid")).toBe(false);
    expect(isGuid("00000000-0000-0000-0000-000000000000")).toBe(false);
  });
});
