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

  await page.goto("/review");
  const status = page.getByRole("combobox", { name: "草稿状态" });
  const queue = page.getByRole("complementary", {
    name: "待审核草稿",
  });
  await status.selectOption("draft");
  await queue.getByRole("button", { name: new RegExp(title) }).click();
  await page.getByRole("button", { name: "提交审核" }).click();
  await status.selectOption("pending_review");
  await queue.getByRole("button", { name: new RegExp(title) }).click();
  await page.getByRole("button", { name: "批准并写入" }).click();
  await expect(
    queue.getByRole("button", { name: new RegExp(title) }),
  ).toHaveCount(0);
};

test("creates a synthesis scaffold and exposes a consistent AI task", async ({
  page,
}) => {
  await ensureUnlocked(page);
  const suffix = Date.now().toString();
  const first = `Synthesis first ${suffix}`;
  const second = `Synthesis second ${suffix}`;
  const topic = `Cross-source review ${suffix}`;
  const question = "What evidence overlaps and what conflicts?";
  await createAndApplyKnowledge(page, first);
  await createAndApplyKnowledge(page, second);

  await page.goto("/knowledge");
  await page.getByRole("checkbox", { name: `选择 ${second}` }).check();
  await page.getByRole("checkbox", { name: `选择 ${first}` }).check();
  await page.getByRole("button", { name: "生成知识综合草稿" }).click();
  const dialog = page.getByRole("dialog", { name: "生成知识综合草稿" });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByLabel("目标知识类型")).toHaveValue(/[0-9a-f-]{36}/);
  await dialog.getByPlaceholder("例如：跨来源证据综合").fill(topic);
  await dialog
    .getByPlaceholder("可选。留空时使用默认综合说明。")
    .fill(question);
  await assertNoSeriousAxeViolations(page, "synthesis dialog");
  await dialog.getByRole("button", { name: "创建综合草稿" }).click();

  await expect(dialog.getByText("知识综合草稿已创建。")).toBeVisible();
  const task = await dialog.getByLabel("外部 AI 综合任务").inputValue();
  expect(task).toContain("synthesize_selected_knowledge");
  expect(task).toContain(topic);
  expect(task).toContain(question);
  expect(task).toContain("文章 (article");
  expect(task.indexOf(second)).toBeLessThan(task.indexOf(first));
  await dialog.getByRole("button", { name: "复制外部 AI 综合任务" }).click();

  await dialog.getByRole("link", { name: "打开综合草稿" }).click();
  await expect(page).toHaveURL(/\/review\?.*status=draft.*draftId=/);
  await expect(
    page.getByRole("heading", { name: `${topic} · 知识综合` }),
  ).toBeVisible();
  const preview = JSON.parse((await page.locator("pre").textContent())!);
  const content = preview.contentMarkdown as string;
  expect(content).toContain(question);
  expect(content).toContain("## 七、关联知识");
  expect(content.indexOf(second)).toBeLessThan(content.indexOf(first));
  await assertNoSeriousAxeViolations(page, "synthesis review");
});

test("synthesis form remains usable on mobile", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await ensureUnlocked(page);
  const suffix = Date.now().toString();
  const title = `Mobile synthesis ${suffix}`;
  await createAndApplyKnowledge(page, title);

  await page.goto("/knowledge");
  await page.getByRole("checkbox", { name: `选择 ${title}` }).check();
  await page.getByRole("button", { name: "生成知识综合草稿" }).click();
  const dialog = page.getByRole("dialog", { name: "生成知识综合草稿" });
  await expect(dialog).toBeVisible();
  await dialog
    .getByPlaceholder("例如：跨来源证据综合")
    .fill("Mobile synthesis");
  const hasHorizontalOverflow = await page.evaluate(
    () =>
      document.documentElement.scrollWidth >
      document.documentElement.clientWidth,
  );
  expect(hasHorizontalOverflow).toBe(false);
  await assertNoSeriousAxeViolations(page, "synthesis mobile dialog");
});
