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

  await status.selectOption("pending_review");
  await reviewQueue.getByRole("button", { name: new RegExp(title) }).click();
  await page.getByRole("button", { name: "批准并写入" }).click();
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
