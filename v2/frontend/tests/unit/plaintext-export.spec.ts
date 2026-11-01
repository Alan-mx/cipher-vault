import { afterEach, describe, expect, it, vi } from "vitest";
import { exportApi } from "../../src/api/exports";

describe("plaintext export download", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    document.body.replaceChildren();
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

    exportApi.submitPlaintextExport("导出明文", "csrf-token");

    expect(fetchSpy).not.toHaveBeenCalled();
    expect(captured).toEqual({
      action: "http://localhost:3000/api/exports/plaintext",
      method: "post",
      target: expect.stringMatching(/^cipher-vault-export-/),
      fields: {
        confirmation: "导出明文",
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
