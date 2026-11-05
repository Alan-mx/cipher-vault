import { flushPromises, mount } from "@vue/test-utils";
import { createMemoryHistory, createRouter } from "vue-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import KnowledgeLibraryView from "../../src/views/KnowledgeLibraryView.vue";
import { knowledgeBaseApi } from "../../src/api/knowledge-base";
import { configureApiClient } from "../../src/api/client";
import type {
  KnowledgeModel,
  KnowledgeSummary,
} from "../../src/types/knowledge-base";

const articleTypeId = "00000000-0000-4000-8000-000000001002";
const noteTypeId = "00000000-0000-4000-8000-000000001001";

const knowledge = (id: string, title: string): KnowledgeSummary => ({
  knowledgeId: id,
  title,
  summary: "",
  knowledgeTypeId: noteTypeId,
  knowledgeTypeKey: "note",
  tags: [],
  status: "active",
  revision: 1,
  updatedAt: "2026-11-05T00:00:00Z",
});

const model = (articleEnabled = true): KnowledgeModel => ({
  version: 1,
  updatedAt: "2026-11-05T00:00:00Z",
  knowledgeTypes: [
    {
      typeId: noteTypeId,
      key: "note",
      name: "笔记",
      description: null,
      isEnabled: true,
      sortOrder: 10,
      properties: [],
    },
    {
      typeId: articleTypeId,
      key: "article",
      name: "文章",
      description: null,
      isEnabled: articleEnabled,
      sortOrder: 20,
      properties: [],
    },
  ],
  templates: [],
  relationTypes: [],
  sourceRules: [],
  workflows: [],
});

const mountView = async () => {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: "/knowledge", component: { template: "" } }],
  });
  await router.push("/knowledge");
  await router.isReady();
  return mount(KnowledgeLibraryView, {
    global: { plugins: [router] },
  });
};

const mockKnowledge = () =>
  vi.spyOn(knowledgeBaseApi, "knowledge").mockResolvedValue({
    items: [
      knowledge("knowledge-1", "First"),
      knowledge("knowledge-2", "Second"),
    ],
    nextCursor: null,
    hasMore: false,
    totalCount: 2,
  });

const clickButton = async (
  wrapper: Awaited<ReturnType<typeof mountView>>,
  label: string,
) =>
  wrapper
    .findAll("button")
    .find((button) => button.text() === label)!
    .trigger("click");

describe("Knowledge synthesis", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    Object.defineProperty(navigator, "clipboard", {
      value: undefined,
      configurable: true,
    });
    configureApiClient({
      getCsrfToken: () => null,
      refreshCsrfToken: async () => {
        throw new Error("unconfigured");
      },
      currentSessionGeneration: () => 0,
      onSessionInvalid: () => false,
    });
  });

  it("defaults to article and falls back to the first enabled type", async () => {
    mockKnowledge();
    vi.spyOn(knowledgeBaseApi, "knowledgeModel").mockResolvedValue(model());
    const wrapper = await mountView();
    await flushPromises();
    await wrapper.findAll('input[type="checkbox"]')[0]!.setValue(true);
    await clickButton(wrapper, "生成知识综合草稿");
    await flushPromises();

    expect(
      (wrapper.find(".synthesis-modal select").element as HTMLSelectElement)
        .value,
    ).toBe(articleTypeId);

    wrapper.unmount();
    vi.restoreAllMocks();
    mockKnowledge();
    vi.spyOn(knowledgeBaseApi, "knowledgeModel").mockResolvedValue(
      model(false),
    );
    const fallback = await mountView();
    await flushPromises();
    await fallback.findAll('input[type="checkbox"]')[0]!.setValue(true);
    await clickButton(fallback, "生成知识综合草稿");
    await flushPromises();
    expect(
      (fallback.find(".synthesis-modal select").element as HTMLSelectElement)
        .value,
    ).toBe(noteTypeId);
  });

  it("disables synthesis when no Knowledge type is enabled", async () => {
    mockKnowledge();
    const disabledModel = model();
    vi.spyOn(knowledgeBaseApi, "knowledgeModel").mockResolvedValue({
      ...disabledModel,
      knowledgeTypes: disabledModel.knowledgeTypes.map((type) => ({
        ...type,
        isEnabled: false,
      })),
    });
    const wrapper = await mountView();
    await flushPromises();
    await wrapper.findAll('input[type="checkbox"]')[0]!.setValue(true);

    expect(wrapper.text()).toContain("知识模型没有启用中的知识类型");
    expect(
      wrapper
        .findAll("button")
        .find((button) => button.text() === "生成知识综合草稿")!
        .attributes("disabled"),
    ).toBeDefined();
  });

  it("keeps M2 and M3 form state independent", async () => {
    mockKnowledge();
    vi.spyOn(knowledgeBaseApi, "knowledgeModel").mockResolvedValue(model());
    const wrapper = await mountView();
    await flushPromises();
    await wrapper.findAll('input[type="checkbox"]')[0]!.setValue(true);
    await clickButton(wrapper, "生成复盘草稿");
    await wrapper
      .find('input[placeholder="例如：v1.3 P0 交付复盘"]')
      .setValue("Retro topic");
    const retrospective = wrapper.find(".modal:not(.synthesis-modal)");
    await retrospective
      .findAll("button")
      .find((button) => button.text() === "取消")!
      .trigger("click");

    await clickButton(wrapper, "生成知识综合草稿");
    await wrapper
      .find('input[placeholder="例如：跨来源证据综合"]')
      .setValue("Synthesis topic");
    await wrapper
      .find('textarea[placeholder="可选。留空时使用默认综合说明。"]')
      .setValue("Compare");
    const synthesis = wrapper.find(".synthesis-modal");
    await synthesis
      .findAll("button")
      .find((button) => button.text() === "取消")!
      .trigger("click");

    await clickButton(wrapper, "生成复盘草稿");
    expect(
      (
        wrapper.find('input[placeholder="例如：v1.3 P0 交付复盘"]')
          .element as HTMLInputElement
      ).value,
    ).toBe("Retro topic");
  });

  it("creates a consistent handoff and supports copy fallback", async () => {
    mockKnowledge();
    vi.spyOn(knowledgeBaseApi, "knowledgeModel").mockResolvedValue(model());
    const create = vi
      .spyOn(knowledgeBaseApi, "createKnowledgeSynthesisDraft")
      .mockResolvedValue({
        draftId: "draft-synthesis",
        draftRevision: 1,
        status: "draft",
        targetObjectType: "knowledge",
        operationType: "create",
        updatedAt: "2026-11-05T00:00:00Z",
      });
    Object.defineProperty(navigator, "clipboard", {
      value: {
        writeText: vi.fn().mockRejectedValue(new Error("denied")),
      },
      configurable: true,
    });
    const wrapper = await mountView();
    await flushPromises();
    await wrapper.findAll('input[type="checkbox"]')[1]!.setValue(true);
    await wrapper.findAll('input[type="checkbox"]')[0]!.setValue(true);
    await clickButton(wrapper, "生成知识综合草稿");
    await wrapper
      .find('input[placeholder="例如：跨来源证据综合"]')
      .setValue("Synthesis topic");
    await wrapper
      .find('textarea[placeholder="可选。留空时使用默认综合说明。"]')
      .setValue("What overlaps?");
    await wrapper.find(".synthesis-modal form").trigger("submit");
    await flushPromises();

    expect(create).toHaveBeenCalledWith(
      {
        topic: "Synthesis topic",
        question: "What overlaps?",
        knowledgeIds: ["knowledge-2", "knowledge-1"],
        knowledgeTypeId: articleTypeId,
      },
      expect.any(String),
    );
    const handoff = (
      wrapper.find('textarea[aria-label="外部 AI 综合任务"]')
        .element as HTMLTextAreaElement
    ).value;
    expect(handoff).toContain("synthesize_selected_knowledge");
    expect(handoff).toContain("Synthesis topic");
    expect(handoff).toContain("What overlaps?");
    expect(handoff).toContain("文章 (article");
    expect(handoff.indexOf("Second")).toBeLessThan(handoff.indexOf("First"));

    await clickButton(wrapper, "复制外部 AI 综合任务");
    await flushPromises();
    expect(wrapper.text()).toContain("无法自动复制");
  });

  it("sends the synthesis API contract with shared headers", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(
        JSON.stringify({
          draftId: "draft-1",
          draftRevision: 1,
          status: "draft",
          targetObjectType: "knowledge",
          operationType: "create",
          updatedAt: "2026-11-05T00:00:00Z",
        }),
        {
          status: 201,
          headers: { "Content-Type": "application/json" },
        },
      ),
    );
    configureApiClient({
      getCsrfToken: () => "csrf-token",
      refreshCsrfToken: async () => "csrf-token",
      currentSessionGeneration: () => 1,
      onSessionInvalid: () => false,
    });

    await knowledgeBaseApi.createKnowledgeSynthesisDraft(
      {
        topic: "Synthesis",
        question: "Question",
        knowledgeIds: ["knowledge-1"],
        knowledgeTypeId: articleTypeId,
      },
      "idempotency-key",
    );

    const [url, request] = fetchMock.mock.calls[0]!;
    const headers = request?.headers as Headers;
    expect(url).toBe("/api/knowledge/synthesis-draft");
    expect(request?.method).toBe("POST");
    expect(headers.get("X-CSRF-TOKEN")).toBe("csrf-token");
    expect(headers.get("Idempotency-Key")).toBe("idempotency-key");
    expect(JSON.parse(String(request?.body))).toMatchObject({
      topic: "Synthesis",
      question: "Question",
      knowledgeIds: ["knowledge-1"],
      knowledgeTypeId: articleTypeId,
    });
  });
});
