import { describe, expect, it } from "vitest";
import { buildMcpConfig } from "../../src/utils/mcp-config";

describe("buildMcpConfig", () => {
  it("builds a token-free streamable HTTP configuration template", () => {
    const config = JSON.parse(buildMcpConfig("http://127.0.0.1:43127", "/mcp"));

    expect(config).toEqual({
      transport: "streamable_http",
      url: "http://127.0.0.1:43127/mcp",
      headers: {
        Authorization: "Bearer <MCP_TOKEN>",
      },
    });
    expect(JSON.stringify(config)).not.toContain("real-token");
  });
});
