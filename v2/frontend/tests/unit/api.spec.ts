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
      onSessionInvalid: () => false,
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
      onSessionInvalid: () => false,
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
    expect(onSessionInvalid).toHaveBeenCalledWith(
      expect.any(ApiClientError),
      1,
    );
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
      onSessionInvalid: () => false,
    });

    await expect(
      new ApiClient().request("/api/documents", { sessionGeneration: 1 }),
    ).rejects.toBeInstanceOf(StaleSessionResponseError);
  });

  it.each([401, 423])(
    "does not clear a newer session after a stale %i response",
    async (status) => {
      let generation = 1;
      const onSessionInvalid = vi.fn(() => false);
      vi.spyOn(globalThis, "fetch").mockImplementation(async () => {
        generation = 2;
        return new Response(
          JSON.stringify({
            code: "vault_locked",
            message: "Vault is locked.",
          }),
          {
            status,
            headers: { "Content-Type": "application/json" },
          },
        );
      });
      configureApiClient({
        getCsrfToken: () => null,
        refreshCsrfToken: async () => "",
        currentSessionGeneration: () => generation,
        onSessionInvalid,
      });

      await expect(
        new ApiClient().request("/api/documents", {
          sessionGeneration: 1,
        }),
      ).rejects.toBeInstanceOf(StaleSessionResponseError);

      expect(onSessionInvalid).not.toHaveBeenCalled();
    },
  );

  it("maps a late network error to a stale response", async () => {
    let generation = 1;
    vi.spyOn(globalThis, "fetch").mockImplementation(async () => {
      generation = 2;
      throw new TypeError("network down");
    });
    configureApiClient({
      getCsrfToken: () => null,
      refreshCsrfToken: async () => "",
      currentSessionGeneration: () => generation,
      onSessionInvalid: () => false,
    });

    await expect(
      new ApiClient().request("/api/documents", {
        sessionGeneration: 1,
      }),
    ).rejects.toBeInstanceOf(StaleSessionResponseError);
  });

  it("does not retry CSRF after the session generation changes", async () => {
    let generation = 1;
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(
        JSON.stringify({
          code: "antiforgery_invalid",
          message: "CSRF expired.",
        }),
        {
          status: 400,
          headers: { "Content-Type": "application/json" },
        },
      ),
    );
    const refreshCsrfToken = vi.fn(async () => {
      generation = 2;
      return "stale-token";
    });
    configureApiClient({
      getCsrfToken: () => "old-token",
      refreshCsrfToken,
      currentSessionGeneration: () => generation,
      onSessionInvalid: () => false,
    });

    await expect(
      new ApiClient().request("/api/documents", {
        method: "POST",
        csrf: true,
        sessionGeneration: 1,
      }),
    ).rejects.toBeInstanceOf(StaleSessionResponseError);

    expect(refreshCsrfToken).toHaveBeenCalledWith(1);
    expect(fetchMock).toHaveBeenCalledOnce();
  });

  it("refreshes CSRF once and retries with the new token", async () => {
    const fetchMock = vi
      .spyOn(globalThis, "fetch")
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            code: "antiforgery_invalid",
            message: "CSRF expired.",
          }),
          {
            status: 400,
            headers: { "Content-Type": "application/json" },
          },
        ),
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ ok: true }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
      );
    const refreshCsrfToken = vi.fn(async () => "new-token");
    let csrfToken = "old-token";
    configureApiClient({
      getCsrfToken: () => csrfToken,
      refreshCsrfToken: async (expectedGeneration) => {
        csrfToken = await refreshCsrfToken(expectedGeneration);
        return csrfToken;
      },
      currentSessionGeneration: () => 1,
      onSessionInvalid: () => false,
    });

    await expect(
      new ApiClient().request("/api/documents", {
        method: "POST",
        csrf: true,
        sessionGeneration: 1,
      }),
    ).resolves.toEqual({ ok: true });

    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(refreshCsrfToken).toHaveBeenCalledOnce();
    const retryHeaders = fetchMock.mock.calls[1]?.[1]?.headers as Headers;
    expect(retryHeaders.get("X-CSRF-TOKEN")).toBe("new-token");
  });

  it("turns a stale parse failure into a stale response", async () => {
    let generation = 1;
    vi.spyOn(globalThis, "fetch").mockImplementation(async () => {
      generation = 2;
      return new Response("{not-json", {
        status: 200,
        headers: { "Content-Type": "application/json" },
      });
    });
    configureApiClient({
      getCsrfToken: () => null,
      refreshCsrfToken: async () => "",
      currentSessionGeneration: () => generation,
      onSessionInvalid: () => false,
    });

    await expect(
      new ApiClient().request("/api/documents", {
        sessionGeneration: 1,
      }),
    ).rejects.toBeInstanceOf(StaleSessionResponseError);
  });
});
