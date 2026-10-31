import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import DocumentList from "../../src/components/document/DocumentList.vue";
import { collectFolders } from "../../src/utils/paths";

describe("catalog helpers", () => {
  it("collects every folder prefix in stable order", () => {
    expect(
      collectFolders(["Root.md", "Work/Project/One.md", "Work/Two.md"]),
    ).toEqual(["Work", "Work/Project"]);
  });
});

describe("DocumentList", () => {
  it("renders metadata and document links without body content", () => {
    const wrapper = mount(DocumentList, {
      props: {
        items: [
          {
            documentId: "00000000-0000-0000-0000-000000000001",
            title: "Project Notes",
            virtualPath: "Work/Project Notes.md",
            format: "md",
            revision: 3,
            tags: ["Work"],
            updatedAt: "2026-10-31T00:00:00Z",
          },
        ],
      },
      global: {
        stubs: {
          RouterLink: {
            template: "<a><slot /></a>",
          },
        },
      },
    });

    expect(wrapper.text()).toContain("Project Notes");
    expect(wrapper.text()).toContain("R3");
    expect(wrapper.text()).toContain("Work");
    expect(wrapper.find("a").exists()).toBe(true);
  });
});
