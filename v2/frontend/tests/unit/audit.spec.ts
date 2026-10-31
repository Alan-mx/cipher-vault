import { describe, expect, it } from "vitest";
import { formatAuditDetails } from "../../src/utils/audit";

describe("audit details formatting", () => {
  it("pretty prints safe JSON and preserves malformed input as text", () => {
    expect(formatAuditDetails('{"tags":2}')).toContain('"tags": 2');
    expect(formatAuditDetails("plain details")).toBe("plain details");
  });
});
