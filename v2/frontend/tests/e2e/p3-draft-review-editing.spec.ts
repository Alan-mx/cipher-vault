import { randomUUID } from "node:crypto";
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

const password = "p0-correct horse battery";

const assertNoSeriousAxeViolations = async (
  page: Page,
  label: string,
): Promise<void> => {
  const results = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa"])
    .analyze();
  const serious = results.violations.filter(
    (violation) =>
      violation.impact === "serious" || violation.impact === "critical",
  );
  expect(
    serious,
    `${label}: ${serious.map((item) => item.id).join(", ")}`,
  ).toEqual([]);
};

const ensureUnlocked = async (page: Page): Promise<void> => {
  await page.goto("/home");
  await page.waitForLoadState("networkidle");
  if (page.url().endsWith("/setup")) {
    await page.getByPlaceholder("至少 12 个字符").fill(password);
    await page.getByPlaceholder("再次输入密码").fill(password);
    await page.getByRole("checkbox").check();
    await page.getByRole("button", { name: "创建 Vault" }).click();
    await expect(page).toHaveURL(/\/unlock$/);
  }
  if (page.url().endsWith("/unlock")) {
    await page.getByPlaceholder("输入密码").fill(password);
    await page.getByRole("button", { name: "解锁", exact: true }).click();
  }
  const hasSessionCookie = (await page.context().cookies()).some(
    (cookie) => cookie.name === "CipherVault.Session",
  );
  if (!hasSessionCookie) {
    const unlock = await page.request.post("/api/vault/unlock", {
      data: { password },
    });
    expect(unlock.ok()).toBe(true);
    await page.goto("/home");
    await page.waitForLoadState("networkidle");
  }
  await expect(page).toHaveURL(/\/home$/);
};

const createKnowledgeDraft = async (
  page: Page,
  title: string,
): Promise<string> => {
  await page.goto("/inbox");
  await page.getByPlaceholder("标题").fill(title);
  await page.getByPlaceholder("记录想法、摘录或待整理内容…").fill(`# ${title}`);
  await page.getByRole("button", { name: "保存到草稿" }).click();
  await expect(page.getByPlaceholder("标题")).toHaveValue("");

  const response = await page.request.get("/api/knowledge?status=active");
  expect(response.ok()).toBe(true);
  const list = (await response.json()) as {
    items: Array<{ knowledgeId: string; title: string }>;
  };
  expect(list.items.some((item) => item.title === title)).toBe(false);

  const drafts = await page.request.get(
    "/api/drafts?status=draft&pageSize=100",
  );
  const draftPage = (await drafts.json()) as {
    items: Array<{ draftId: string; title: string }>;
  };
  const draft = draftPage.items.find((item) => item.title === title);
  expect(draft).toBeTruthy();
  return draft!.draftId;
};

const submitAndApplyDraft = async (
  page: Page,
  title: string,
): Promise<void> => {
  const status = page.getByRole("combobox", { name: "草稿状态" });
  const queue = page.getByRole("complementary", {
    name: "待审核草稿",
  });
  await page.goto("/review");
  await status.selectOption("draft");
  await queue.getByRole("button", { name: new RegExp(title) }).click();
  await page.getByRole("button", { name: "提交审核" }).click();
  await status.selectOption("pending_review");
  await queue.getByRole("button", { name: new RegExp(title) }).click();
  await page.getByRole("button", { name: "批准并写入" }).click();
};

const submitAndApplyDraftById = async (
  page: Page,
  draftId: string,
): Promise<void> => {
  const detail = await page.request.get(`/api/drafts/${draftId}`);
  const detailJson = (await detail.json()) as {
    record: { draftRevision: number };
  };
  const csrfResponse = await page.request.get("/api/vault/antiforgery");
  const csrf = ((await csrfResponse.json()) as { token: string }).token;
  const submitted = await page.request.post(
    `/api/drafts/${draftId}/submit-review`,
    {
      headers: {
        "X-CSRF-TOKEN": csrf,
        "Idempotency-Key": randomUUID(),
      },
      data: {
        expectedDraftRevision: detailJson.record.draftRevision,
      },
    },
  );
  expect(submitted.ok()).toBe(true);
  const submittedJson = (await submitted.json()) as {
    draftRevision: number;
  };
  const applied = await page.request.post(`/api/drafts/${draftId}/apply`, {
    headers: {
      "X-CSRF-TOKEN": csrf,
      "Idempotency-Key": randomUUID(),
    },
    data: {
      expectedDraftRevision: submittedJson.draftRevision,
    },
  });
  expect(applied.ok()).toBe(true);
};

test("edits a Knowledge draft before submit and apply", async ({ page }) => {
  await ensureUnlocked(page);
  const suffix = Date.now().toString();
  const title = `Editable Knowledge ${suffix}`;
  const editedTitle = `Edited Knowledge ${suffix}`;
  const draftId = await createKnowledgeDraft(page, title);

  await page.goto("/review");
  const status = page.getByRole("combobox", { name: "草稿状态" });
  const queue = page.getByRole("complementary", {
    name: "待审核草稿",
  });
  await status.selectOption("draft");
  await queue.getByRole("button", { name: new RegExp(title) }).click();
  await page.getByLabel("标题").fill(editedTitle);
  await page
    .getByLabel("正文 Markdown")
    .fill(`# ${editedTitle}\n\nEdited body.`);
  await page.getByRole("button", { name: "保存 Draft" }).click();
  await expect(page.getByText("Draft 已保存。")).toBeVisible();
  await expect(page.getByText("字段变化")).toBeVisible();
  await assertNoSeriousAxeViolations(page, "draft knowledge editor");

  await submitAndApplyDraftById(page, draftId);

  await page.goto("/knowledge");
  await expect(page.getByText(editedTitle)).toBeVisible();
});

test("editing a pending review draft returns it to draft", async ({ page }) => {
  await ensureUnlocked(page);
  const suffix = Date.now().toString();
  const title = `Pending Edit ${suffix}`;
  const editedTitle = `Pending Edited ${suffix}`;
  await createKnowledgeDraft(page, title);

  await page.goto("/review");
  const status = page.getByRole("combobox", { name: "草稿状态" });
  const queue = page.getByRole("complementary", {
    name: "待审核草稿",
  });
  await status.selectOption("draft");
  await queue.getByRole("button", { name: new RegExp(title) }).click();
  await page.getByRole("button", { name: "提交审核" }).click();
  await expect(status).toHaveValue("pending_review");
  await queue.getByRole("button", { name: new RegExp(title) }).click();

  await page.getByLabel("标题").fill(editedTitle);
  await page.getByRole("button", { name: "保存 Draft" }).click();
  await expect(
    page.getByText("修改已保存，Draft 已退回处理中，请重新送审。"),
  ).toBeVisible();
  await expect(status).toHaveValue("draft");
  await expect(page.getByRole("button", { name: "提交审核" })).toBeVisible();

  await page.getByRole("button", { name: "提交审核" }).click();
  await status.selectOption("pending_review");
  await queue.getByRole("button", { name: new RegExp(editedTitle) }).click();
  await page.getByRole("button", { name: "批准并写入" }).click();
});

test("edits Source and Relation drafts", async ({ page }) => {
  await ensureUnlocked(page);
  const suffix = Date.now().toString();

  await page.goto("/sources");
  await page.getByPlaceholder("来源标题").fill(`Source Edit ${suffix}`);
  await page.getByPlaceholder("https://…").fill("https://example.com/edit");
  await page.getByPlaceholder("来源内容快照").fill("# Source\n\nOriginal.");
  await page.getByRole("button", { name: "保存为草稿" }).click();
  await page.goto("/review");
  const status = page.getByRole("combobox", { name: "草稿状态" });
  const queue = page.getByRole("complementary", {
    name: "待审核草稿",
  });
  await status.selectOption("draft");
  await queue
    .getByRole("button", { name: new RegExp(`Source Edit ${suffix}`) })
    .click();
  await page.getByLabel("标题").fill(`Source Edited ${suffix}`);
  await page.getByLabel("内容快照").fill("# Source\n\nUpdated.");
  await page.getByRole("button", { name: "保存 Draft" }).click();
  await expect(page.getByText("Draft 已保存。")).toBeVisible();

  await createKnowledgeDraft(page, `Relation A ${suffix}`);
  await createKnowledgeDraft(page, `Relation B ${suffix}`);
  await submitAndApplyDraft(page, `Relation A ${suffix}`);
  await submitAndApplyDraft(page, `Relation B ${suffix}`);
  const knowledgePage = await page.request.get(
    "/api/knowledge?status=active&pageSize=100",
  );
  const knowledge = (await knowledgePage.json()) as {
    items: Array<{
      knowledgeId: string;
      title: string;
      revision: number;
    }>;
  };
  const firstKnowledge = knowledge.items.find((item) =>
    item.title.startsWith(`Relation A ${suffix}`),
  )!;
  const secondKnowledge = knowledge.items.find((item) =>
    item.title.startsWith(`Relation B ${suffix}`),
  )!;
  const csrfResponse = await page.request.get("/api/vault/antiforgery");
  const csrf = ((await csrfResponse.json()) as { token: string }).token;
  const relationResponse = await page.request.post("/api/drafts", {
    headers: {
      "X-CSRF-TOKEN": csrf,
      "Idempotency-Key": randomUUID(),
    },
    data: {
      targetObjectType: "relation",
      operationType: "create",
      payload: {
        relationTypeId: "00000000-0000-4000-8000-000000002001",
        sourceKnowledgeId: firstKnowledge.knowledgeId,
        targetKnowledgeId: secondKnowledge.knowledgeId,
        sourceKnowledgeRevision: firstKnowledge.revision,
        targetKnowledgeRevision: secondKnowledge.revision,
        rationale: "Original relation reason.",
        evidence: [],
      },
    },
  });
  expect(relationResponse.ok()).toBe(true);
  const relationDraft = (await relationResponse.json()) as {
    draftId: string;
  };
  await page.goto(
    `/review?status=draft&draftId=${encodeURIComponent(relationDraft.draftId)}`,
  );
  await page.getByLabel("关系理由").fill("Updated relation reason.");
  await page.getByRole("button", { name: "保存 Draft" }).click();
  await expect(page.getByText("Draft 已保存。")).toBeVisible();
});

test("draft editor remains usable on mobile", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await ensureUnlocked(page);
  const title = `Mobile Edit ${Date.now()}`;
  await createKnowledgeDraft(page, title);
  await page.goto("/review?status=draft");
  const queue = page.getByRole("complementary", {
    name: "待审核草稿",
  });
  await queue.getByRole("button", { name: new RegExp(title) }).click();
  const overflow = await page.evaluate(
    () =>
      document.documentElement.scrollWidth >
      document.documentElement.clientWidth,
  );
  expect(overflow).toBe(false);
  await assertNoSeriousAxeViolations(page, "mobile draft editor");
});
