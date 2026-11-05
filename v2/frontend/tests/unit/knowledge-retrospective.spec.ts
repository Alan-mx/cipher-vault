import { flushPromises, mount } from "@vue/test-utils";
import { createMemoryHistory, createRouter } from "vue-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import KnowledgeLibraryView from "../../src/views/KnowledgeLibraryView.vue";
import UnifiedReviewView from "../../src/views/UnifiedReviewView.vue";
import { knowledgeBaseApi } from "../../src/api/knowledge-base";
import { configureApiClient } from "../../src/api/client";
import type {
  DraftDetail,
  KnowledgeSummary,
} from "../../src/types/knowledge-base";

const knowledge = (id: string, title: string): KnowledgeSummary => ({
  knowledgeId: id,
  title,
  summary: "",
  knowledgeTypeId: "type-id",
  knowledgeTypeKey: "note",
  tags: [],
  status: "active",
  revision: 1,
  updatedAt: "2026-11-05T00:00:00Z",
});

const knowledgeIds = Array.from({ length: 21 }, (_, index) =>
  (index + 1).toString().padStart(8, "0"),
);

const mountKnowledgeView = async () => {
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

describe("Knowledge retrospective", () => {
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

  it("sends the retrospective API contract", async () => {
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

    await knowledgeBaseApi.createKnowledgeRetrospectiveDraft(
      { topic: "Review", knowledgeIds: ["knowledge-1"] },
      "idempotency-key",
    );

    const [url, request] = fetchMock.mock.calls[0]!;
    const headers = request?.headers as Headers;
    expect(url).toBe("/api/knowledge/retrospective-draft");
    expect(request?.method).toBe("POST");
    expect(headers.get("X-CSRF-TOKEN")).toBe("csrf-token");
    expect(headers.get("Idempotency-Key")).toBe("idempotency-key");
    expect(JSON.parse(String(request?.body))).toEqual({
      topic: "Review",
      knowledgeIds: ["knowledge-1"],
    });
  });

  it("preserves click order and enforces the 20 item limit", async () => {
    vi.spyOn(knowledgeBaseApi, "knowledge").mockResolvedValue({
      items: knowledgeIds.map((id, index) =>
        knowledge(id, `Knowledge ${index + 1}`),
      ),
      nextCursor: null,
      hasMore: false,
      totalCount: 21,
    });
    const wrapper = await mountKnowledgeView();
    await flushPromises();

    const checkboxes = wrapper.findAll('input[type="checkbox"]');
    await checkboxes[2]!.setValue(true);
    await checkboxes[0]!.setValue(true);
    await checkboxes[4]!.setValue(true);
    expect(wrapper.vm.selectedIds).toEqual([
      knowledgeIds[2],
      knowledgeIds[0],
      knowledgeIds[4],
    ]);

    for (let index = 5; index < 20; index++) {
      await checkboxes[index]!.setValue(true);
    }
    await checkboxes[1]!.setValue(true);
    await checkboxes[3]!.setValue(true);
    expect(wrapper.vm.selectedIds.length).toBe(20);
    expect(checkboxes[20]!.attributes("disabled")).toBeDefined();
  });

  it("sends CSRF and reuses the idempotency key after failure", async () => {
    vi.spyOn(knowledgeBaseApi, "knowledge").mockResolvedValue({
      items: [knowledge("knowledge-1", "Knowledge")],
      nextCursor: null,
      hasMore: false,
      totalCount: 1,
    });
    const requestSpy = vi
      .spyOn(knowledgeBaseApi, "createKnowledgeRetrospectiveDraft")
      .mockRejectedValueOnce(new Error("network down"))
      .mockResolvedValueOnce({
        draftId: "draft-1",
        draftRevision: 1,
        status: "draft",
        targetObjectType: "knowledge",
        operationType: "create",
        updatedAt: "2026-11-05T00:00:00Z",
      });
    const randomSpy = vi
      .spyOn(crypto, "randomUUID")
      .mockReturnValue("same-idempotency-key");
    const wrapper = await mountKnowledgeView();
    await flushPromises();
    await wrapper.find('input[type="checkbox"]').setValue(true);
    await wrapper
      .findAll("button")
      .find((button) => button.text() === "生成复盘草稿")!
      .trigger("click");
    await flushPromises();
    await wrapper
      .find('input[placeholder="例如：v1.3 P0 交付复盘"]')
      .setValue("Review topic");
    await wrapper.find(".modal form").trigger("submit");
    await flushPromises();
    await wrapper.find(".modal form").trigger("submit");
    await flushPromises();

    expect(requestSpy).toHaveBeenCalledTimes(2);
    expect(requestSpy.mock.calls[0]?.[1]).toBe("same-idempotency-key");
    expect(requestSpy.mock.calls[1]?.[1]).toBe("same-idempotency-key");
    expect(wrapper.text()).toContain("复盘草稿已创建");
    expect(randomSpy).toHaveBeenCalledOnce();
  });

  it("rejects an empty or oversized topic before submit", async () => {
    vi.spyOn(knowledgeBaseApi, "knowledge").mockResolvedValue({
      items: [knowledge("knowledge-1", "Knowledge")],
      nextCursor: null,
      hasMore: false,
      totalCount: 1,
    });
    const requestSpy = vi
      .spyOn(knowledgeBaseApi, "createKnowledgeRetrospectiveDraft")
      .mockResolvedValue({
        draftId: "draft-1",
        draftRevision: 1,
        status: "draft",
        targetObjectType: "knowledge",
        operationType: "create",
        updatedAt: "2026-11-05T00:00:00Z",
      });
    const wrapper = await mountKnowledgeView();
    await flushPromises();
    await wrapper.find('input[type="checkbox"]').setValue(true);
    await wrapper
      .findAll("button")
      .find((button) => button.text() === "生成复盘草稿")!
      .trigger("click");
    await flushPromises();
    await wrapper.find(".modal form").trigger("submit");
    await flushPromises();

    expect(requestSpy).not.toHaveBeenCalled();

    await wrapper
      .find('input[placeholder="例如：v1.3 P0 交付复盘"]')
      .setValue("x".repeat(194));
    await wrapper.find(".modal form").trigger("submit");
    await flushPromises();
    expect(requestSpy).not.toHaveBeenCalled();
  });

  it("opens the deep-linked draft and syncs review query", async () => {
    const detail = {
      record: {
        draftId: "draft-deep-link",
        draftRevision: 1,
        targetObjectType: "knowledge",
        operationType: "create",
        targetId: null,
        baseRevision: null,
        baseModelVersion: 1,
        payloadVersion: 1,
        payload: {},
        baseSnapshot: null,
        status: "draft",
        sourceActor: "user_ui",
        createdAt: "2026-11-05T00:00:00Z",
        updatedAt: "2026-11-05T00:00:00Z",
        terminalAt: null,
        appliedObjectType: null,
        appliedObjectId: null,
        appliedObjectRevision: null,
        appliedAt: null,
      },
      preview: {
        summary: "Knowledge draft",
        diff: "",
        diffTruncated: false,
        sourceReferences: [],
        changes: [],
        changesTruncated: false,
        validationIssues: [],
      },
    } satisfies DraftDetail;
    vi.spyOn(knowledgeBaseApi, "drafts").mockResolvedValue({
      items: [],
      nextCursor: null,
      hasMore: false,
      totalCount: 0,
    });
    vi.spyOn(knowledgeBaseApi, "draft").mockResolvedValue(detail);
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: "/review", component: UnifiedReviewView }],
    });
    await router.push({
      path: "/review",
      query: { status: "draft", draftId: "draft-deep-link" },
    });
    await router.isReady();
    const wrapper = mount(UnifiedReviewView, {
      global: { plugins: [router] },
    });
    await flushPromises();

    expect(knowledgeBaseApi.draft).toHaveBeenCalledWith("draft-deep-link");
    expect(wrapper.text()).toContain("Knowledge draft");
    expect(router.currentRoute.value.query.draftId).toBe("draft-deep-link");
  });
});
