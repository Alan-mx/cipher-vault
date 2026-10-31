import { mount } from "@vue/test-utils";
import { createPinia } from "pinia";
import { describe, expect, it } from "vitest";
import CreateDocumentDialog from "../../src/components/document/CreateDocumentDialog.vue";
import { NForm, NFormItem, NInput } from "naive-ui";

describe("CreateDocumentDialog", () => {
  it("Naive NInput v-model responds to native input", async () => {
    const wrapper = mount({
      components: { NInput },
      template: '<NInput v-model:value="value" placeholder="probe" />',
      data: () => ({ value: "" }),
    });
    const input = wrapper.get('input[placeholder="probe"]');
    await input.setValue("probe-value");
    expect((input.element as HTMLInputElement).value).toBe("probe-value");
  });

  it("Naive NInput v-model responds inside NFormItem", async () => {
    const wrapper = mount({
      components: { NForm, NFormItem, NInput },
      template:
        '<NForm><NFormItem label="Probe"><NInput v-model:value="value" placeholder="form-probe" /></NFormItem></NForm>',
      data: () => ({ value: "" }),
    });
    const input = wrapper.get('input[placeholder="form-probe"]');
    await input.setValue("form-value");
    expect((input.element as HTMLInputElement).value).toBe("form-value");
  });

  it("keeps bound input values", async () => {
    const wrapper = mount(CreateDocumentDialog, {
      props: { show: true },
      global: {
        plugins: [createPinia()],
        stubs: {
          teleport: true,
          NModal: {
            template: "<div><slot /></div>",
          },
        },
      },
    });

    const title = wrapper.get('input[placeholder="文档标题"]');
    await title.setValue("Alpha");

    expect((title.element as HTMLInputElement).value).toBe("Alpha");
  });
});
