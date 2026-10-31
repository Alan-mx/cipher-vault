import { describe, expect, it } from "vitest";
import { isPurgeEligible } from "../../src/utils/trash";

describe("trash purge eligibility", () => {
  it("allows permanent deletion only at or after purgeEligibleAt", () => {
    const eligibleAt = "2026-10-31T00:00:00Z";

    expect(isPurgeEligible(eligibleAt, Date.parse(eligibleAt) - 1)).toBe(false);
    expect(isPurgeEligible(eligibleAt, Date.parse(eligibleAt))).toBe(true);
  });
});
