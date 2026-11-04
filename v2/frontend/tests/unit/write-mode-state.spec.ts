import { describe, expect, it } from "vitest";
import { writeModeDisplayState } from "../../src/utils/write-mode-state";

describe("write mode display state", () => {
  it("distinguishes loading, unknown and known modes", () => {
    expect(writeModeDisplayState(null, true, false)).toBe("loading");
    expect(writeModeDisplayState(null, false, false)).toBe("unknown");
    expect(writeModeDisplayState(null, false, true)).toBe("unknown");
    expect(
      writeModeDisplayState(
        {
          mode: "direct",
          effective: true,
        },
        true,
        false,
      ),
    ).toBe("loading");
    expect(
      writeModeDisplayState(
        {
          mode: "draft",
          effective: true,
        },
        false,
        true,
      ),
    ).toBe("unknown");
    expect(
      writeModeDisplayState(
        {
          mode: "draft",
          effective: true,
        },
        false,
        false,
      ),
    ).toBe("draft");
    expect(
      writeModeDisplayState(
        {
          mode: "direct",
          effective: true,
        },
        false,
        false,
      ),
    ).toBe("direct");
  });
});
