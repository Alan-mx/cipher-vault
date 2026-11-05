import { flushPromises, mount } from "@vue/test-utils";
import { createMemoryHistory, createRouter } from "vue-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import { knowledgeBaseApi } from "../../src/api/knowledge-base";
import type {
  DashboardView,
  KnowledgeModel,
  KnowledgeTriagePage,
} from "../../src/types/knowledge-base";
import HomeView from "../../src/views/HomeView.vue";
import TriageView from "../../src/views/TriageView.vue";

const model: KnowledgeModel = {
  version: 1,
  updatedAt: "2026-11-05T00:00:00Z",
  knowledgeTypes: [
    {
      typeId: "type-1",
      key: "article",
      name: "文章",
      description: null,
      isEnabled: true,
      sortOrder: 10,
      properties: [],
    },
  ],
  templates: [],
  relationTypes: [],
  sourceRules: [],
  workflows: [],
};

const triagePage = (): KnowledgeTriagePage => ({
  items: [
    {
      knowledgeId: "knowledge-1",
      title: "Distributed systems",
      knowledgeTypeId: "type-1",
      typeKey: "article",
      typeName: "文章",
      tags: ["distributed"],
      revision: 3,
      updatedAt: "2026-09-01T00:00:00Z",
      reasonCodes: ["missing_required_property", "missing_source", "stale"],
      missingPropertyKeys: ["author"],
      sourceReferenceCount: 0,
    },
  ],
  nextCursor: null,
  hasMore: false,
  counts: {
    missingSource: 8,
    missingRequiredProperty: 15,
    stale: 12,
    totalDistinct: 24,
    draft: 3,
    conflict: 1,
  },
});

const dashboard: DashboardView = {
  recentKnowledge: [],
  recentSources: [],
  pendingDrafts: [],
  inbox: [],
  recentRelationChanges: [],
  counts: {},
};

const mountTriage = async () => {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: "/triage", component: TriageView }],
  });
  await router.push("/triage");
  await router.isReady();
  const wrapper = mount(TriageView, {
    global: { plugins: [router] },
  });
  await flushPromises();
  return { wrapper, router };
};

describe("Knowledge triage", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    Object.defineProperty(navigator, "clipboard", {
      value: undefined,
      configurable: true,
    });
  });

  it("renders counts, reasons and missing properties", async () => {
    vi.spyOn(knowledgeBaseApi, "knowledgeModel").mockResolvedValue(model);
    vi.spyOn(knowledgeBaseApi, "triage").mockResolvedValue(triagePage());
    const { wrapper } = await mountTriage();

    expect(wrapper.text()).toContain("Distributed systems");
    expect(wrapper.text()).toContain("缺少来源");
    expect(wrapper.text()).toContain("缺少必填属性");
    expect(wrapper.text()).toContain("长期未更新");
    expect(wrapper.text()).toContain("缺少属性：author");
    expect(wrapper.text()).toContain("3 处理中 · 1 冲突");
  });

  it("filters by reason and updates the URL", async () => {
    vi.spyOn(knowledgeBaseApi, "knowledgeModel").mockResolvedValue(model);
    const triage = vi
      .spyOn(knowledgeBaseApi, "triage")
      .mockResolvedValue(triagePage());
    const { wrapper, router } = await mountTriage();
    triage.mockClear();

    await wrapper
      .findAll("button")
      .find((button) => button.text().includes("缺少来源"))!
      .trigger("click");
    await flushPromises();

    expect(triage).toHaveBeenCalledWith(
      expect.objectContaining({ reason: "missing_source" }),
    );
    expect(router.currentRoute.value.query.reason).toBe("missing_source");
  });

  it("falls back to selectable task text when copy fails", async () => {
    vi.spyOn(knowledgeBaseApi, "knowledgeModel").mockResolvedValue(model);
    vi.spyOn(knowledgeBaseApi, "triage").mockResolvedValue(triagePage());
    Object.defineProperty(navigator, "clipboard", {
      value: {
        writeText: vi.fn().mockRejectedValue(new Error("denied")),
      },
      configurable: true,
    });
    const { wrapper } = await mountTriage();

    await wrapper
      .findAll("button")
      .find((button) => button.text() === "复制 AI 整理任务")!
      .trigger("click");
    await flushPromises();

    expect(wrapper.text()).toContain("无法自动复制");
    const text = (
      wrapper.find('textarea[aria-label="AI 整理任务文本"]')
        .element as HTMLTextAreaElement
    ).value;
    expect(text).toContain("review_knowledge_quality");
    expect(text).toContain("knowledge-1");
    expect(text).toContain("author");
    expect(text).toContain("Draft-only");
  });

  it("shows an explicit empty state", async () => {
    vi.spyOn(knowledgeBaseApi, "knowledgeModel").mockResolvedValue(model);
    vi.spyOn(knowledgeBaseApi, "triage").mockResolvedValue({
      ...triagePage(),
      items: [],
      counts: {
        missingSource: 0,
        missingRequiredProperty: 0,
        stale: 0,
        totalDistinct: 0,
        draft: 0,
        conflict: 0,
      },
    });
    const { wrapper } = await mountTriage();

    expect(wrapper.text()).toContain("当前没有待整理知识");
  });

  it("shows a triage summary on the home page", async () => {
    vi.spyOn(knowledgeBaseApi, "dashboard").mockResolvedValue(dashboard);
    vi.spyOn(knowledgeBaseApi, "triage").mockResolvedValue(triagePage());
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: "/home", component: HomeView }],
    });
    await router.push("/home");
    await router.isReady();
    const wrapper = mount(HomeView, {
      global: { plugins: [router] },
    });
    await flushPromises();

    expect(wrapper.text()).toContain("知识整理");
    expect(wrapper.text()).toContain("24 条待处理");
    expect(wrapper.find('a[href="/triage"]').exists()).toBe(true);
  });
});
