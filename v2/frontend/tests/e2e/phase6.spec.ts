import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const password = "phase6-correct horse";
const currentDirectory = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(currentDirectory, "../..");
const artifactRoot = resolve(frontendRoot, "../artifacts/e2e");
const screenshotRoot = resolve(artifactRoot, "screenshots");

const assertNoSeriousAxeViolations = async (page: Page, label: string) => {
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

const assertNoHorizontalOverflow = async (page: Page): Promise<void> => {
  const hasOverflow = await page.evaluate(
    () =>
      document.documentElement.scrollWidth >
      document.documentElement.clientWidth,
  );
  expect(hasOverflow).toBe(false);
};

test("Phase 6 desktop flow, responsive layout and accessibility", async ({
  page,
}) => {
  page.on("pageerror", (error) => {
    console.error("PAGE_ERROR", error);
  });
  page.on("console", (message) => {
    if (message.type() === "error") {
      console.error("CONSOLE_ERROR", message.text());
    }
  });
  mkdirSync(screenshotRoot, { recursive: true });
  const importRoot = resolve(artifactRoot, "import-source");
  mkdirSync(importRoot, { recursive: true });
  writeFileSync(
    resolve(importRoot, "Imported.md"),
    "---\ntags: [Imported]\n---\n# Imported\n\nImported needle",
    "utf8",
  );

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto("/");
  await expect(page).toHaveURL(/\/setup$/);
  await page.screenshot({
    path: resolve(screenshotRoot, "setup-1280.png"),
    fullPage: true,
  });
  await assertNoSeriousAxeViolations(page, "setup desktop");

  await page.getByPlaceholder("至少 12 个字符").fill(password);
  await page.getByPlaceholder("再次输入密码").fill(password);
  await page.getByRole("checkbox").check();
  await page.getByRole("button", { name: "创建 Vault" }).click();

  await expect(page).toHaveURL(/\/unlock$/);
  await page.getByPlaceholder("输入密码").fill(password);
  await page.getByRole("button", { name: "解锁", exact: true }).click();
  await expect(page).toHaveURL(/\/documents$/);
  await page.screenshot({
    path: resolve(screenshotRoot, "documents-1280.png"),
    fullPage: true,
  });
  await page.waitForTimeout(3_500);
  await assertNoSeriousAxeViolations(page, "documents desktop");

  await page.getByRole("button", { name: "新建文档" }).first().click();
  await expect(page.getByRole("dialog", { name: "新建文档" })).toBeVisible();
  await page.waitForTimeout(500);
  const titleInput = page.getByPlaceholder("文档标题");
  const folderInput = page.getByPlaceholder("例如 Notes/Projects");
  const contentInput = page.getByPlaceholder("输入 Markdown 或 TXT 内容");
  await titleInput.fill("Alpha");
  await folderInput.fill("Work");
  await contentInput.fill("# Alpha\n\nNeedle one");
  await expect(titleInput).toHaveValue("Alpha");
  await expect(contentInput).toHaveValue("# Alpha\n\nNeedle one");
  await page.getByRole("button", { name: "创建", exact: true }).click();
  await expect(page).toHaveURL(/\/documents\/[0-9a-f-]+$/);
  const documentUrl = page.url();
  await expect(page.locator(".cm-content")).toBeVisible();
  await page.screenshot({
    path: resolve(screenshotRoot, "editor-1280.png"),
    fullPage: true,
  });

  await page.locator(".cm-content").fill("# Alpha\n\nNeedle two");
  await page.keyboard.press("Control+s");
  await expect(
    page.locator(".document-footer").getByText("已保存", { exact: true }),
  ).toBeVisible();
  await page.reload();
  await expect(page.locator(".cm-content")).toContainText("Needle two");

  await page.getByText("版本", { exact: true }).click();
  await expect(page.locator(".revision-row")).toHaveCount(2);
  await page.locator(".revision-row").nth(0).getByRole("checkbox").check();
  await page.locator(".revision-row").nth(1).getByRole("checkbox").check();
  await page.getByRole("button", { name: "对比", exact: true }).click();
  await expect(page.locator(".revision-diff")).toContainText("+Needle one");

  await page.getByRole("button", { name: "搜索文档" }).click();
  await page.getByPlaceholder("输入全文搜索词…").fill("Needle");
  await expect(page.getByRole("button", { name: /Alpha/ })).toBeVisible();
  await page.getByRole("button", { name: "关闭搜索" }).click();

  await page.getByRole("button", { name: "知识网络" }).first().click();
  await expect(page).toHaveURL(/\/graph$/);
  await page.locator(".graph-controls .n-base-selection").first().click();
  await page.getByText("Alpha", { exact: true }).last().click();
  await expect(page.locator(".graph-canvas canvas").first()).toBeVisible();
  await page.screenshot({
    path: resolve(screenshotRoot, "graph-1280.png"),
    fullPage: true,
  });

  await page.goto("/tools/import");
  await page
    .getByPlaceholder("例如 D:\\Notes 或 /home/user/notes")
    .fill(importRoot);
  await page.getByRole("checkbox").check();
  await page.getByRole("button", { name: "生成预览" }).click();
  await expect(page.getByText("预览已生成")).toBeVisible();
  await page.getByRole("button", { name: "提交导入" }).click();
  await page.getByRole("button", { name: "确认" }).click();
  await expect(page.getByText("导入完成", { exact: false })).toBeVisible();

  await page.goto("/audit");
  await expect(page.getByRole("heading", { name: "活动记录" })).toBeVisible();
  await page.getByRole("button", { name: "查询" }).click();
  await expect(page.locator(".audit-row").first()).toBeVisible();

  await page.goto("/settings");
  await expect(
    page.getByText("CIPHERVAULT_IDLE_TIMEOUT_MINUTES"),
  ).toBeVisible();
  await expect(page.getByText("0.0.0-local")).toBeVisible();
  await page.getByRole("button", { name: "创建备份" }).click();
  await expect(page.locator(".backup-row")).toHaveCount(1);
  await expect(page.locator(".backup-row")).toContainText("已验证");
  await expect(page.locator(".backup-download")).toHaveAttribute(
    "href",
    /^\/api\/backups\/cipher-vault-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}\.cvbackup\/download$/,
  );
  await page.getByRole("button", { name: /生成 Token|轮换 Token/ }).click();
  await page.getByRole("button", { name: "确认" }).click();
  await expect(page.getByText("MCP Token 已轮换")).toBeVisible();
  await expect(page.locator(".token-value input")).not.toHaveValue("");
  await page.getByRole("button", { name: "我已保存，关闭" }).click();
  await expect(page.getByText(/已配置 · Version/)).toBeVisible();
  await expect(page.locator(".token-dialog")).toBeHidden();
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({
    path: resolve(screenshotRoot, "settings-1280.png"),
    fullPage: true,
  });
  await assertNoSeriousAxeViolations(page, "settings desktop");

  await page.goto(documentUrl);
  await page.getByRole("button", { name: "删除", exact: true }).click();
  await page.getByRole("button", { name: "确认" }).click();
  await expect(page).toHaveURL(/\/documents$/);
  await page.goto("/trash");
  await expect(page.getByText("Alpha", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "恢复", exact: true }).click();
  await expect(page.getByText("文档已恢复。")).toBeVisible();

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/documents");
  await expect(page.getByRole("button", { name: "打开导航" })).toBeVisible();
  await page.screenshot({
    path: resolve(screenshotRoot, "documents-390.png"),
    fullPage: true,
  });
  await assertNoHorizontalOverflow(page);
  await page.getByRole("button", { name: "搜索文档" }).click();
  await expect(page.getByPlaceholder("输入全文搜索词…")).toBeVisible();
  await page.getByRole("button", { name: "关闭搜索" }).click();
  await page.goto(documentUrl);
  await expect(page.locator(".cm-content")).toBeVisible();
  await page.screenshot({
    path: resolve(screenshotRoot, "editor-390.png"),
    fullPage: true,
  });
  await assertNoHorizontalOverflow(page);
  await assertNoSeriousAxeViolations(page, "editor mobile");

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(documentUrl);
  await expect(page.locator(".cm-content")).toBeVisible();
  await page.getByRole("button", { name: "锁定", exact: true }).click();
  await expect(page).toHaveURL(/\/unlock$/);
  await expect(page.locator(".cm-content")).toHaveCount(0);
  await page.screenshot({
    path: resolve(screenshotRoot, "lock-1280.png"),
    fullPage: true,
  });
  await assertNoSeriousAxeViolations(page, "lock desktop");
});
