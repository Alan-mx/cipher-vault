import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import BootView from "../../src/views/BootView.vue";

describe("BootView", () => {
  it("renders the local Vault boot state", () => {
    const wrapper = mount(BootView);

    expect(wrapper.text()).toContain("CipherVault");
    expect(wrapper.text()).toContain("本地加密知识库");
    expect(wrapper.text()).toContain("正在连接本地 Vault");
  });
});
