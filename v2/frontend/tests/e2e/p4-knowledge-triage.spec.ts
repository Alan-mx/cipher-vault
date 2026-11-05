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

const createAndApplyKnowledge = async (
  page: Page,
  title: string,
): Promise<void> => {
  await page.goto("/inbox");
  await page.getByPlaceholder("标题").fill(title);
  await page.getByPlaceholder("记录想法、摘录或待整理内容…").fill(`# ${title}`);
  await page.getByRole("button", { name: "保存到草稿" }).click();
  await expect(page.getByPlaceholder("标题")).toHaveValue("");
  const drafts = await page.request.get(
    "/api/drafts?status=draft&pageSize=100",
  );
  const pageJson = (await drafts.json()) as {
    items: Array<{ draftId: string; title: string }>;
  };
  const draftId = pageJson.items.find((item) => item.title === title)!.draftId;
  const csrfResponse = await page.request.get("/api/vault/antiforgery");
  const csrf = ((await csrfResponse.json()) as { token: string }).token;
  const submitted = await page.request.post(
    `/api/drafts/${draftId}/submit-review`,
    {
      headers: {
        "X-CSRF-TOKEN": csrf,
        "Idempotency-Key": randomUUID(),
      },
      data: { expectedDraftRevision: 1 },
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
    data: { expectedDraftRevision: submittedJson.draftRevision },
  });
  expect(applied.ok()).toBe(true);
};

test("triage discovers a Knowledge without source and exposes the AI task", async ({
  page,
}) => {
  await ensureUnlocked(page);
  const title = `Triage missing source ${Date.now()}`;
  await createAndApplyKnowledge(page, title);

  await page.goto("/triage");
  await expect(page.getByRole("heading", { name: "知识整理" })).toBeVisible();
  await expect(page.getByText(title)).toBeVisible();
  await expect(
    page.getByText("缺少来源", { exact: true }).first(),
  ).toBeVisible();
  await assertNoSeriousAxeViolations(page, "knowledge triage");

  await page.getByRole("button", { name: "复制 AI 整理任务" }).first().click();
  await expect(page.getByRole("region", { name: "AI 整理任务" })).toBeVisible();

  await page.goto("/home");
  await expect(page.getByRole("heading", { name: "知识整理" })).toBeVisible();
  await page.getByRole("link", { name: "开始整理" }).click();
  await expect(page).toHaveURL(/\/triage$/);
});

test("triage remains usable on mobile", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await ensureUnlocked(page);
  await page.goto("/triage");
  await expect(page.getByRole("heading", { name: "知识整理" })).toBeVisible();
  const overflow = await page.evaluate(
    () =>
      document.documentElement.scrollWidth >
      document.documentElement.clientWidth,
  );
  expect(overflow).toBe(false);
  await assertNoSeriousAxeViolations(page, "mobile knowledge triage");
});
