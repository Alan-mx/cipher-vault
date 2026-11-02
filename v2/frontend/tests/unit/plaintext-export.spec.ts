import { afterEach, describe, expect, it, vi } from "vitest";
import { exportApi } from "../../src/api/exports";
import { configureApiClient } from "../../src/api/client";

describe("plaintext export download", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    document.body.replaceChildren();
    configureApiClient({
      getCsrfToken: () => null,
      refreshCsrfToken: async () => {
        throw new Error("unconfigured");
      },
      currentSessionGeneration: () => 0,
      onSessionInvalid: () => false,
    });
  });

  it("preflights through the shared API client and surfaces API errors", async () => {
    configureApiClient({
      getCsrfToken: () => "csrf-token",
      refreshCsrfToken: async () => "csrf-token",
      currentSessionGeneration: () => 1,
      onSessionInvalid: () => false,
    });
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(
        JSON.stringify({
          ticket: "ticket",
          expiresAtUtc: "2026-11-02T00:02:00Z",
          documentCount: 2,
          totalContentBytes: 12,
        }),
        {
          status: 200,
          headers: { "Content-Type": "application/json" },
        },
      ),
    );

    await expect(exportApi.preflight("导出明文")).resolves.toEqual({
      ticket: "ticket",
      expiresAtUtc: "2026-11-02T00:02:00Z",
      documentCount: 2,
      totalContentBytes: 12,
    });
    expect(fetchMock).toHaveBeenCalledOnce();
    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      "/api/exports/plaintext/preflight",
    );
  });

  it("submits a hidden CSRF form without reading the ZIP in JavaScript", () => {
    let captured:
      | {
          action: string;
          method: string;
          target: string;
          fields: Record<string, string>;
        }
      | undefined;
    vi.spyOn(HTMLFormElement.prototype, "submit").mockImplementation(
      function submit(this: HTMLFormElement) {
        captured = {
          action: this.action,
          method: this.method,
          target: this.target,
          fields: Object.fromEntries(
            Array.from(this.elements).map((element) => {
              const input = element as HTMLInputElement;
              return [input.name, input.value];
            }),
          ),
        };
      },
    );
    const fetchSpy = vi.spyOn(globalThis, "fetch");

    exportApi.submitPlaintextExport("ticket", "csrf-token");

    expect(fetchSpy).not.toHaveBeenCalled();
    expect(captured).toEqual({
      action: "http://localhost:3000/api/exports/plaintext/download",
      method: "post",
      target: expect.stringMatching(/^cipher-vault-export-/),
      fields: {
        ticket: "ticket",
        __CipherVaultAntiforgery: "csrf-token",
      },
    });
    expect(
      document.getElementById("cipher-vault-export-download-target"),
    ).toBeInstanceOf(HTMLIFrameElement);
    expect(document.querySelector("body > form")).toBeNull();
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });
});
