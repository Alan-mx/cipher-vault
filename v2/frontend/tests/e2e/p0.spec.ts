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
  await expect(page.getByRole("heading", { name: "首页" })).toBeVisible();
};

const submitAndApplyDraft = async (
  page: Page,
  title: string,
): Promise<void> => {
  const status = page.getByRole("combobox", { name: "草稿状态" });
  const reviewQueue = page.getByRole("complementary", {
    name: "待审核草稿",
  });

  await page.goto("/review");
  await status.selectOption("draft");
  await reviewQueue.getByRole("button", { name: new RegExp(title) }).click();
  await page.getByRole("button", { name: "提交审核" }).click();
  await expect(status).toHaveValue("pending_review");

  const applyButton = page.getByRole("button", { name: "批准并写入" });
  await expect(applyButton).toBeVisible();
  await reviewQueue.getByRole("button", { name: new RegExp(title) }).click();
  const applyResponse = page.waitForResponse(
    (response) =>
      response.request().method() === "POST" &&
      response.url().endsWith("/apply"),
  );
  await applyButton.click();
  expect((await applyResponse).ok()).toBe(true);
  await expect(applyButton).toBeHidden();
};

test("P0 knowledge workflow covers source, inbox, review and model", async ({
  page,
}) => {
  await ensureUnlocked(page);

  await page.goto("/sources");
  await page.getByPlaceholder("来源标题").fill("Source draft");
  await page.getByPlaceholder("https://…").fill("https://example.com/article");
  await page
    .getByPlaceholder("来源内容快照")
    .fill("# Section\n\nParagraph text.");
  await page.getByRole("button", { name: "保存为草稿" }).click();
  await expect(page.getByPlaceholder("来源标题")).toHaveValue("");

  await submitAndApplyDraft(page, "Source draft");
  await expect(page.locator("body")).not.toContainText("Source draft");

  await page.goto("/sources");
  await page.getByRole("button", { name: /Source draft/ }).click();
  await expect(
    page.getByRole("heading", { name: "Source draft" }),
  ).toBeVisible();
  await expect(page.getByRole("combobox", { name: "来源版本" })).toHaveValue(
    "1",
  );
  await page.getByRole("button", { name: /^Section section/ }).click();
  await expect(page.getByText(/定位内容/)).toBeVisible();
  await expect(page.locator(".content pre")).toContainText("Paragraph text.");

  await page.goto("/inbox");
  await page.getByPlaceholder("标题").fill("Knowledge draft");
  await page
    .getByPlaceholder(/记录想法、摘录或待整理内容/)
    .fill("# Knowledge draft");
  await page.getByRole("button", { name: "保存到草稿" }).click();
  await expect(page.getByPlaceholder("标题")).toHaveValue("");
  await submitAndApplyDraft(page, "Knowledge draft");

  await page.goto("/knowledge");
  await expect(page.getByText("Knowledge draft")).toBeVisible();
  await expect(page).toHaveScreenshot("knowledge-library.png");
  await assertNoSeriousAxeViolations(page, "knowledge library");
});

test("P0 settings and home stay usable on mobile", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await ensureUnlocked(page);
  await page.goto("/settings");
  await expect(page.getByRole("heading", { name: "设置" })).toBeVisible();
  await assertNoSeriousAxeViolations(page, "settings mobile");
  await page.goto("/home");
  await expect(page.getByRole("heading", { name: "首页" })).toBeVisible();
  await assertNoSeriousAxeViolations(page, "home mobile");
});

test("MCP setup shows the connection contract and capabilities", async ({
  page,
}) => {
  await ensureUnlocked(page);
  await page.goto("/settings/mcp");

  await expect(page.getByRole("heading", { name: "MCP 接入" })).toBeVisible();
  await expect(
    page.getByText("http://127.0.0.1:43199/mcp", { exact: true }),
  ).toBeVisible();
  await expect(page.getByRole("heading", { name: "Tools" })).toBeVisible();
  await expect(
    page.getByText("search_knowledge", { exact: true }),
  ).toBeVisible();
  await expect(page.getByRole("heading", { name: "Prompts" })).toBeVisible();
  await expect(
    page.getByText("answer_with_knowledge", { exact: true }),
  ).toBeVisible();
  await assertNoSeriousAxeViolations(page, "MCP setup");
});

test("Knowledge Model editor saves a structured type", async ({ page }) => {
  await ensureUnlocked(page);
  await page.goto("/settings/knowledge-model");

  await expect(page.getByRole("heading", { name: "知识模型" })).toBeVisible();
  await page.getByRole("button", { name: "新增类型" }).click();
  await page.getByLabel("类型稳定 key").fill("e2e_note");
  await page.getByLabel("类型名称").fill("E2E Note");
  await page.getByRole("button", { name: "保存模型" }).click();

  await expect(page.getByText(/模型 v\d+ 已保存/)).toBeVisible();
  await expect(page.getByRole("button", { name: /E2E Note/ })).toBeVisible();
  await assertNoSeriousAxeViolations(page, "knowledge model editor");
});
