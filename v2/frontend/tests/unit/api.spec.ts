import { afterEach, describe, expect, it, vi } from "vitest";
import {
  ApiClient,
  buildQueryString,
  configureApiClient,
} from "../../src/api/client";
import {
  ApiClientError,
  StaleSessionResponseError,
} from "../../src/types/domain";

describe("buildQueryString", () => {
  it("serializes repeated tags and omits empty values", () => {
    expect(
      buildQueryString({
        tags: ["Work", "Project"],
        cursor: undefined,
        orphanOnly: false,
        pageSize: 20,
      }),
    ).toBe("?orphanOnly=false&pageSize=20&tags=Work&tags=Project");
  });
});

describe("ApiClient", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    configureApiClient({
      getCsrfToken: () => null,
      refreshCsrfToken: async () => {
        throw new Error("unconfigured");
      },
      currentSessionGeneration: () => 0,
      onSessionInvalid: () => undefined,
    });
  });

  it("sends CSRF and If-Match headers", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(JSON.stringify({ ok: true }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );
    configureApiClient({
      getCsrfToken: () => "csrf-token",
      refreshCsrfToken: async () => "csrf-token",
      currentSessionGeneration: () => 1,
      onSessionInvalid: () => undefined,
    });
    const client = new ApiClient();

    await client.request("/api/documents/id", {
      method: "PUT",
      csrf: true,
      ifMatch: 3,
      body: { title: "x" },
      sessionGeneration: 1,
    });

    const request = fetchMock.mock.calls[0]?.[1];
    const headers = request?.headers as Headers;
    expect(headers.get("X-CSRF-TOKEN")).toBe("csrf-token");
    expect(headers.get("If-Match")).toBe('"3"');
    expect(request?.credentials).toBe("same-origin");
    expect(request?.cache).toBe("no-store");
  });

  it("reports session-invalid responses", async () => {
    const onSessionInvalid = vi.fn();
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(
        JSON.stringify({
          code: "vault_locked",
          message: "Vault is locked.",
          details: {},
          correlationId: "corr",
        }),
        {
          status: 423,
          headers: { "Content-Type": "application/json" },
        },
      ),
    );
    configureApiClient({
      getCsrfToken: () => null,
      refreshCsrfToken: async () => "",
      currentSessionGeneration: () => 1,
      onSessionInvalid,
    });

    await expect(
      new ApiClient().request("/api/documents"),
    ).rejects.toBeInstanceOf(ApiClientError);
    expect(onSessionInvalid).toHaveBeenCalledOnce();
  });

  it("drops responses from an invalidated session generation", async () => {
    let generation = 1;
    vi.spyOn(globalThis, "fetch").mockImplementation(async () => {
      generation = 2;
      return new Response(JSON.stringify({ title: "stale" }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      });
    });
    configureApiClient({
      getCsrfToken: () => null,
      refreshCsrfToken: async () => "",
      currentSessionGeneration: () => generation,
      onSessionInvalid: () => undefined,
    });

    await expect(
      new ApiClient().request("/api/documents", { sessionGeneration: 1 }),
    ).rejects.toBeInstanceOf(StaleSessionResponseError);
  });
});
