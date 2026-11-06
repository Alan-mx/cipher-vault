import { flushPromises, mount } from "@vue/test-utils";
import { createMemoryHistory, createRouter } from "vue-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import { knowledgeBaseApi } from "../../src/api/knowledge-base";
import { ApiClientError } from "../../src/types/domain";
import type {
  DraftDetail,
  KnowledgeModel,
} from "../../src/types/knowledge-base";
import type { KnowledgeDraftPayload } from "../../src/types/draft-editor";
import {
  canonicalizeDraftPayload,
  isDraftPayloadDirty,
  parseDraftPayload,
  serializeDraftPayload,
} from "../../src/utils/draft-editor";
import UnifiedReviewView from "../../src/views/UnifiedReviewView.vue";

const knowledgeTypeId = "00000000-0000-4000-8000-000000001002";

const model: KnowledgeModel = {
  version: 1,
  updatedAt: "2026-11-05T00:00:00Z",
  knowledgeTypes: [
    {
      typeId: knowledgeTypeId,
      key: "article",
      name: "文章",
      description: null,
      isEnabled: true,
      sortOrder: 10,
      properties: [
        {
          propertyId: "property-1",
          key: "status",
          name: "状态",
          valueType: "string",
          required: true,
          defaultValue: "raw",
          constraints: {},
          isEnabled: true,
          sortOrder: 0,
        },
      ],
    },
  ],
  templates: [],
  relationTypes: [],
  sourceRules: [],
  workflows: [],
};

const payload = {
  title: "Original title",
  summary: "Summary",
  knowledgeTypeId,
  templateId: null,
  contentMarkdown: "# Body",
  properties: { status: "raw" },
  tags: ["one"],
  sourceReferences: [],
};

const draftDetail = (status = "draft"): DraftDetail => ({
  record: {
    draftId: "draft-1",
    draftRevision: 2,
    targetObjectType: "knowledge",
    operationType: "update",
    targetId: "knowledge-1",
    baseRevision: 1,
    baseModelVersion: 1,
    payloadVersion: 1,
    payload,
    baseSnapshot: payload,
    status,
    sourceActor: "mcp",
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
    diff: JSON.stringify(payload),
    diffTruncated: false,
    sourceReferences: [],
    changes: [
      {
        fieldPath: "title",
        kind: "modified",
        before: "Old title",
        after: "Original title",
        valueType: "string",
        beforeTruncated: false,
        afterTruncated: false,
      },
    ],
    changesTruncated: false,
    validationIssues: [
      {
        fieldPath: "properties.status",
        message: "Status is required.",
        severity: "error",
      },
    ],
  },
});

const draftPage = {
  items: [],
  nextCursor: null,
  hasMore: false,
  totalCount: 0,
};

const mockReviewApis = (detail: DraftDetail | null = draftDetail()) => {
  const drafts = vi
    .spyOn(knowledgeBaseApi, "drafts")
    .mockResolvedValue(draftPage);
  vi.spyOn(knowledgeBaseApi, "knowledgeModel").mockResolvedValue(model);
  if (detail) {
    vi.spyOn(knowledgeBaseApi, "draft").mockResolvedValue(detail);
  }
  return { drafts };
};

interface MountReviewOptions {
  status?: string;
  draftId?: string | null;
}

const mountReview = async ({
  status = "draft",
  draftId = "draft-1",
}: MountReviewOptions = {}) => {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: "/review", component: UnifiedReviewView }],
  });
  await router.push({
    path: "/review",
    query: {
      status,
      ...(draftId ? { draftId } : {}),
    },
  });
  await router.isReady();
  const wrapper = mount(UnifiedReviewView, {
    global: { plugins: [router] },
  });
  await flushPromises();
  return { wrapper, router };
};

const statusOptions = [
  ["pending_review", "待审核"],
  ["draft", "处理中"],
  ["conflict", "冲突"],
  ["applied", "已应用"],
  ["rejected", "已拒绝"],
  ["discarded", "已丢弃"],
  ["expired", "已过期"],
] as const;

const terminalStatuses = [
  "applied",
  "rejected",
  "discarded",
  "expired",
] as const;

describe("Draft review editing", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("parses, serializes and canonicalizes editable payloads", () => {
    const parsed = parseDraftPayload("knowledge", payload);
    const serialized = serializeDraftPayload("knowledge", parsed);
    expect(serialized).toEqual(payload);

    const changed = {
      ...(parsed as KnowledgeDraftPayload),
      properties: { status: "verified" },
    };
    expect(isDraftPayloadDirty("knowledge", parsed, changed)).toBe(true);
    expect(canonicalizeDraftPayload("knowledge", parsed)).toBe(
      canonicalizeDraftPayload("knowledge", parsed),
    );
  });

  it("renders the seven Draft status filters", async () => {
    mockReviewApis(null);
    const { wrapper } = await mountReview({
      status: "pending_review",
      draftId: null,
    });

    const options = wrapper
      .get('select[aria-label="草稿状态"]')
      .findAll("option")
      .map((option) => [option.attributes("value"), option.text()]);

    expect(options).toEqual(statusOptions);
  });

  it.each(terminalStatuses)(
    "loads the %s history status as an empty queue",
    async (terminalStatus) => {
      const { drafts } = mockReviewApis(null);
      const { wrapper } = await mountReview({
        status: terminalStatus,
        draftId: null,
      });

      expect(drafts).toHaveBeenCalledWith({
        status: terminalStatus,
        pageSize: 100,
      });
      expect(wrapper.text()).toContain("选择一条 Draft 查看详情");
      expect(wrapper.find('[role="alert"]').exists()).toBe(false);
    },
  );

  it.each(terminalStatuses)(
    "shows the %s history detail as read-only",
    async (terminalStatus) => {
      mockReviewApis(draftDetail(terminalStatus));
      const { wrapper } = await mountReview({ status: terminalStatus });

      const buttons = wrapper.findAll("button").map((button) => button.text());
      expect(wrapper.text()).toContain("Knowledge draft");
      expect(buttons).not.toContain("保存 Draft");
      expect(buttons).not.toContain("提交审核");
      expect(buttons).not.toContain("批准并写入");
      expect(buttons).not.toContain("拒绝");
      expect(buttons).not.toContain("丢弃");
    },
  );

  it("falls back to pending_review for an unknown status", async () => {
    const { drafts } = mockReviewApis(null);
    const { router } = await mountReview({
      status: "unknown",
      draftId: null,
    });

    expect(drafts).toHaveBeenCalledWith({
      status: "pending_review",
      pageSize: 100,
    });
    expect(router.currentRoute.value.query.status).toBe("pending_review");
  });

  it("prefers the detail status when status and draftId conflict", async () => {
    const { drafts } = mockReviewApis(draftDetail("applied"));
    const { wrapper, router } = await mountReview({
      status: "rejected",
      draftId: "draft-1",
    });

    expect(drafts).toHaveBeenLastCalledWith({
      status: "applied",
      pageSize: 100,
    });
    expect(router.currentRoute.value.query.status).toBe("applied");
    expect(wrapper.text()).toContain("Knowledge draft");
  });

  it("saves edited Knowledge and refreshes with expected revision", async () => {
    vi.spyOn(knowledgeBaseApi, "drafts").mockResolvedValue({
      items: [],
      nextCursor: null,
      hasMore: false,
      totalCount: 0,
    });
    vi.spyOn(knowledgeBaseApi, "knowledgeModel").mockResolvedValue(model);
    vi.spyOn(knowledgeBaseApi, "draft").mockResolvedValue(draftDetail());
    const update = vi.spyOn(knowledgeBaseApi, "updateDraft").mockResolvedValue({
      draftId: "draft-1",
      draftRevision: 3,
      status: "draft",
      targetObjectType: "knowledge",
      operationType: "update",
      updatedAt: "2026-11-05T01:00:00Z",
    });
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const { wrapper } = await mountReview();

    await wrapper
      .find('input[value="Original title"]')
      .setValue("Edited title");
    await wrapper
      .findAll("button")
      .find((button) => button.text() === "保存 Draft")!
      .trigger("click");
    await flushPromises();

    expect(update).toHaveBeenCalledWith(
      "draft-1",
      2,
      expect.objectContaining({ title: "Edited title" }),
      expect.any(String),
    );
    expect(wrapper.text()).toContain("Draft 已保存");
    expect(wrapper.text()).toContain("字段变化");
    expect(wrapper.text()).toContain("Status is required");
  });

  it("shows reload flow on Draft revision conflict", async () => {
    vi.spyOn(knowledgeBaseApi, "drafts").mockResolvedValue({
      items: [],
      nextCursor: null,
      hasMore: false,
      totalCount: 0,
    });
    vi.spyOn(knowledgeBaseApi, "knowledgeModel").mockResolvedValue(model);
    vi.spyOn(knowledgeBaseApi, "draft").mockResolvedValue(draftDetail());
    vi.spyOn(knowledgeBaseApi, "updateDraft").mockRejectedValue(
      new ApiClientError(409, {
        code: "draft_revision_conflict",
        message: "Draft revision changed.",
      }),
    );
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const { wrapper } = await mountReview();

    await wrapper
      .find('input[value="Original title"]')
      .setValue("Conflict title");
    await wrapper
      .findAll("button")
      .find((button) => button.text() === "保存 Draft")!
      .trigger("click");
    await flushPromises();

    expect(wrapper.text()).toContain("Draft 已在其他位置更新");
    expect(
      wrapper.findAll("button").some((button) => button.text() === "重新加载"),
    ).toBe(true);
  });
});
