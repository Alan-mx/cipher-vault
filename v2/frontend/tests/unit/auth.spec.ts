import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import {
  passwordScalarLength,
  isValidNewPassword,
} from "../../src/utils/password";
import LockFailedView from "../../src/views/LockFailedView.vue";

describe("password policy", () => {
  it("counts unicode scalars and requires at least twelve", () => {
    expect(passwordScalarLength("😀😀😀😀😀😀😀😀😀😀😀😀")).toBe(12);
    expect(isValidNewPassword("😀😀😀😀😀😀😀😀😀😀😀😀")).toBe(true);
    expect(isValidNewPassword("too-short")).toBe(false);
  });
});

describe("LockFailedView", () => {
  it("does not offer an unlock action", () => {
    const wrapper = mount(LockFailedView, {
      global: {
        stubs: {
          AuthLayout: {
            template: "<main><slot /></main>",
          },
        },
      },
    });

    expect(wrapper.text()).toContain("CipherVault.Server");
    expect(wrapper.text()).not.toContain("重新解锁");
  });
});
