import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { mkdirSync, statSync, writeFileSync } from "node:fs";
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

const ensureUnlocked = async (page: Page): Promise<void> => {
  await page.goto("/documents");
  await page.waitForLoadState("networkidle");
  await page.waitForTimeout(500);
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
  await expect(page).toHaveURL(/\/documents$/);
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

  await page.getByRole("button", { name: "复制", exact: true }).click();
  const copyDialog = page.getByRole("dialog", { name: "复制文档" });
  await expect(copyDialog).toBeVisible();
  await expect(copyDialog.locator("input").first()).toHaveValue("Alpha 副本");
  await page
    .getByRole("dialog", { name: "复制文档" })
    .getByRole("button", { name: "复制", exact: true })
    .click();
  await expect(page).not.toHaveURL(documentUrl);
  await expect(page.locator(".cm-content")).toContainText("id:");
  await page.goto(documentUrl);
  await expect(page.locator(".cm-content")).toContainText("Needle two");

  await page.getByText("版本", { exact: true }).click();
  await expect(page.locator(".revision-row")).toHaveCount(2);
  await page.locator(".revision-row").nth(0).getByRole("checkbox").check();
  await page.locator(".revision-row").nth(1).getByRole("checkbox").check();
  await page.getByRole("button", { name: "对比", exact: true }).click();
  await expect(page.locator(".revision-diff")).toContainText("+Needle one");

  await page.evaluate(async () => {
    const token = await fetch("/api/vault/antiforgery")
      .then((response) => response.json())
      .then((body: { token: string }) => body.token);
    const response = await fetch("/api/documents", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-CSRF-TOKEN": token,
      },
      body: JSON.stringify({
        title: "PropertyDoc",
        virtualPath: "notes/property-doc.md",
        format: "md",
        content:
          "---\nstatus: verified\nsource:\n  kind: project\n---\n\n# PropertyDoc\n\nNeedle property",
      }),
    });
    if (!response.ok) {
      throw new Error(`Property document create failed: ${response.status}`);
    }
  });
  const linkedDocuments = await page.evaluate(async () => {
    const token = await fetch("/api/vault/antiforgery")
      .then((response) => response.json())
      .then((body: { token: string }) => body.token);
    const create = async (body: object) => {
      const response = await fetch("/api/documents", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-CSRF-TOKEN": token,
        },
        body: JSON.stringify(body),
      });
      if (!response.ok) {
        throw new Error(`Linked document create failed: ${response.status}`);
      }
      return response.json() as Promise<{ documentId: string }>;
    };
    const target = await create({
      title: "Link Target",
      virtualPath: "notes/link-target.md",
      format: "md",
      content: "# Link Target\n\n## Redis 缓存\n\nTarget body",
    });
    const source = await create({
      title: "Link Source",
      virtualPath: "docs/link-source.md",
      format: "md",
      content:
        "[Jump](../notes/link-target.md#Redis%20缓存)\n\n[[Link Target]]\n\n[Missing](missing.md)",
    });
    return { targetId: target.documentId, sourceId: source.documentId };
  });
  const relatedDocuments = await page.evaluate(async () => {
    const token = await fetch("/api/vault/antiforgery")
      .then((response) => response.json())
      .then((body: { token: string }) => body.token);
    const create = async (body: object) => {
      const response = await fetch("/api/documents", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-CSRF-TOKEN": token,
        },
        body: JSON.stringify(body),
      });
      if (!response.ok) {
        throw new Error(`Related document create failed: ${response.status}`);
      }
      return response.json() as Promise<{ documentId: string }>;
    };
    const candidate = await create({
      title: "Related Candidate",
      virtualPath: "related/candidate.md",
      format: "md",
      content: "---\nproject: CipherVault\n---\n\nCandidate body",
    });
    const direct = await create({
      title: "Related Direct",
      virtualPath: "related/direct.md",
      format: "md",
      content: "---\nproject: CipherVault\n---\n\nDirect body",
    });
    const source = await create({
      title: "Related Source",
      virtualPath: "related/source.md",
      format: "md",
      content: "---\nproject: CipherVault\n---\n\n[[Related Direct]]",
    });
    return {
      candidateId: candidate.documentId,
      directId: direct.documentId,
      sourceId: source.documentId,
    };
  });
  await page.goto(`/documents/${linkedDocuments.sourceId}`);
  await page.getByText("预览", { exact: true }).click();
  const markdownLink = page.locator("a.document-link", { hasText: "Jump" });
  await expect(markdownLink).toBeVisible();
  await expect(
    page.locator("a.document-link", { hasText: "Link Target" }),
  ).toBeVisible();
  await expect(page.locator("a", { hasText: "Missing" })).toHaveCount(0);
  await markdownLink.click();
  await expect(page).toHaveURL(
    new RegExp(
      `/documents/${linkedDocuments.targetId}#cv-heading-REDIS%20%E7%BC%93%E5%AD%98$`,
    ),
  );
  await expect(
    page.locator('[id="cv-heading-REDIS%20%E7%BC%93%E5%AD%98"]'),
  ).toBeVisible();
  await page.goto(`/documents/${relatedDocuments.sourceId}`);
  await expect(page.locator(".cm-content")).toBeVisible();
  await page.getByText("相关", { exact: true }).click();
  await expect(
    page.locator(".related-panel").getByRole("button", {
      name: /Related Candidate/,
    }),
  ).toBeVisible();
  await expect(
    page.locator(".related-panel").getByText("Related Direct", {
      exact: true,
    }),
  ).toHaveCount(0);
  await page.getByRole("button", { name: "刷新相关推荐" }).click();
  await expect(
    page.locator(".related-panel").getByRole("button", {
      name: /Related Candidate/,
    }),
  ).toBeVisible();
  await page
    .locator(".related-panel")
    .getByRole("button", { name: /Related Candidate/ })
    .click();
  await expect(page).toHaveURL(
    new RegExp(`/documents/${relatedDocuments.candidateId}$`),
  );
  await page.goto(`/documents/${relatedDocuments.sourceId}`);
  await page.getByRole("button", { name: "搜索文档" }).click();
  await page.getByPlaceholder("输入全文搜索词…").fill("Needle");
  await expect(
    page.getByRole("button", { name: /Alpha/ }).first(),
  ).toBeVisible();
  await page.getByPlaceholder("输入全文搜索词…").fill("");
  await page.getByRole("button", { name: "添加属性条件" }).click();
  await page.getByPlaceholder("例如 status / source.kind").fill("source.kind");
  await page
    .locator('.property-row input[aria-label="属性值"]')
    .fill("project");
  await page.getByText("任一满足", { exact: true }).click();
  await expect(page.getByRole("button", { name: /PropertyDoc/ })).toBeVisible();
  await page.getByRole("button", { name: "删除属性条件" }).click();
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
  await expect(page.getByRole("heading", { name: "导出明文" })).toBeVisible();
  await expect(page.getByText("下载后将失去 Vault 加密保护")).toBeVisible();
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

  await page.getByRole("button", { name: "直接写入" }).click();
  await page.getByRole("button", { name: "确认开启" }).click();
  await expect(page.getByText("MCP 直接写入已开启。")).toBeVisible();
  await page.getByRole("button", { name: "草稿模式" }).click();
  await expect(page.getByText("MCP 已恢复草稿模式。")).toBeVisible();

  const draftSetup = await page.evaluate(async () => {
    const token = await fetch("/api/vault/antiforgery")
      .then((response) => response.json())
      .then((body: { token: string }) => body.token);
    const created = await fetch("/api/drafts", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-CSRF-TOKEN": token,
        "Idempotency-Key": crypto.randomUUID(),
      },
      body: JSON.stringify({
        draftType: "create",
        payload: {
          title: "Review Draft",
          virtualPath: "notes/review-draft.md",
          format: "md",
          content: "# Review Draft\n\nInitial proposal",
          tags: [],
        },
      }),
    }).then((response) => {
      if (!response.ok) {
        throw new Error(`Draft create failed: ${response.status}`);
      }
      return response.json();
    });
    const reviewed = await fetch(
      `/api/drafts/${created.draftId}/submit-review`,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-CSRF-TOKEN": token,
          "Idempotency-Key": crypto.randomUUID(),
        },
        body: JSON.stringify({
          expectedDraftRevision: created.draftRevision,
        }),
      },
    ).then((response) => {
      if (!response.ok) {
        throw new Error(`Draft submit failed: ${response.status}`);
      }
      return response.json();
    });
    return reviewed as {
      draftId: string;
      draftRevision: number;
    };
  });
  expect(draftSetup.draftId).toMatch(/^[0-9a-f-]+$/);

  await page.goto("/review");
  await expect(page.getByRole("heading", { name: "待审核" })).toBeVisible();
  await page.getByRole("button", { name: /Review Draft/ }).click();
  await expect(page.locator(".draft-detail textarea")).toBeVisible();
  await page
    .locator(".draft-detail textarea")
    .fill("# Review Draft\n\nReviewed proposal");
  await page.getByRole("button", { name: "保存编辑" }).click();
  await expect(page.getByText("草稿已保存。")).toBeVisible();
  await page.getByRole("button", { name: "送审" }).click();
  await expect(page.getByText("草稿已进入待审核。")).toBeVisible();
  await page.getByRole("button", { name: "提交正式文档" }).click();
  await page.getByRole("button", { name: "确认" }).click();
  await expect(page.getByText(/已提交为正式文档/)).toBeVisible();

  await page.goto(documentUrl);
  await page.getByRole("button", { name: "删除", exact: true }).click();
  await page.getByRole("button", { name: "确认" }).click();
  await expect(page).toHaveURL(/\/documents$/);
  await page.goto("/trash");
  await expect(page.getByText("Alpha", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "恢复", exact: true }).click();
  await expect(page.getByText("文档已恢复。")).toBeVisible();

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/review");
  await expect(page.getByRole("heading", { name: "待审核" })).toBeVisible();
  await assertNoHorizontalOverflow(page);
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

  await page.goto(`/documents/${relatedDocuments.sourceId}`);
  await page.getByRole("button", { name: "文档信息" }).click();
  const inspectorDrawer = page.getByRole("dialog");
  await inspectorDrawer.getByText("相关", { exact: true }).click();
  await expect(
    inspectorDrawer.locator(".related-panel").getByRole("button", {
      name: /Related Candidate/,
    }),
  ).toBeVisible();
  await assertNoHorizontalOverflow(page);

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

test("M13 export ticket flow and M09 pageshow revalidation", async ({
  page,
}) => {
  const lockRequests: string[] = [];
  page.on("request", (request) => {
    if (
      request.method() === "POST" &&
      request.url().endsWith("/api/vault/lock")
    ) {
      lockRequests.push(request.url());
    }
  });
  await ensureUnlocked(page);
  await page.goto("/settings");
  await expect(page.getByRole("heading", { name: "导出明文" })).toBeVisible();
  await page.getByPlaceholder("导出明文").fill("导出明文");

  await page.route(
    "**/api/exports/plaintext/preflight",
    (route) =>
      route.fulfill({
        status: 400,
        contentType: "application/json",
        body: JSON.stringify({
          code: "export_too_large",
          message: "导出预检失败：内容超过限制。",
          details: {},
          correlationId: "m14",
        }),
      }),
    { times: 1 },
  );
  page.once("dialog", (dialog) => dialog.accept());
  await page.getByRole("button", { name: "下载明文 ZIP" }).click();
  await expect(page.getByText("导出预检失败：内容超过限制。")).toBeVisible();

  const downloadResponsePromise = page.waitForResponse((response) =>
    response.url().endsWith("/api/exports/plaintext/download"),
  );
  page.once("dialog", (dialog) => dialog.accept());
  await page.getByRole("button", { name: "下载明文 ZIP" }).click();
  const downloadResponse = await downloadResponsePromise;
  if (!downloadResponse.ok()) {
    throw new Error(
      `Download response failed: ${downloadResponse.status()} ${await downloadResponse.text()}`,
    );
  }
  const download = await page.waitForEvent("download");

  expect(download.suggestedFilename()).toMatch(
    /^cipher-vault-export-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}\.zip$/,
  );
  const downloadPath = await download.path();
  expect(downloadPath).not.toBeNull();
  expect(statSync(downloadPath!).size).toBeGreaterThan(0);
  await expect(
    page.getByText(
      "下载请求已提交，请查看浏览器下载列表；页面无法确认文件是否已完整下载。",
    ),
  ).toBeVisible();

  const statusResponse = page.waitForResponse((response) =>
    response.url().endsWith("/api/vault/status"),
  );
  await page.evaluate(() => {
    window.dispatchEvent(
      new PageTransitionEvent("pagehide", { persisted: false }),
    );
    window.dispatchEvent(
      new PageTransitionEvent("pageshow", { persisted: true }),
    );
  });
  expect((await statusResponse).ok()).toBe(true);
  await expect(page).toHaveURL(/\/settings$/);
  expect(lockRequests).toEqual([]);
});

test("Knowledge views support facets, active projects and deep links", async ({
  page,
}) => {
  await ensureUnlocked(page);
  await page.evaluate(async (unlockPassword) => {
    const response = await fetch("/api/vault/unlock", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ password: unlockPassword }),
    });
    if (!response.ok) {
      throw new Error(`Knowledge view auth failed: ${response.status}`);
    }
  }, password);
  const documents = await page.evaluate(async () => {
    const token = await fetch("/api/vault/antiforgery")
      .then((response) => response.json())
      .then((body: { token: string }) => body.token);
    const create = async (body: object) => {
      const response = await fetch("/api/documents", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-CSRF-TOKEN": token,
        },
        body: JSON.stringify(body),
      });
      if (!response.ok) {
        throw new Error(`Knowledge view fixture failed: ${response.status}`);
      }
      return response.json() as Promise<{ documentId: string }>;
    };
    const first = await create({
      title: "Views Project A One",
      virtualPath: "views/project-a-one.md",
      format: "md",
      content:
        "---\nproject: Views Project A\nstatus: active\n" +
        "type: technical-note\nmaturity: verified\n---\n\nOne.",
    });
    const second = await create({
      title: "Views Project A Two",
      virtualPath: "views/project-a-two.md",
      format: "md",
      content:
        "---\nproject: views project a\nstatus: active\n" +
        "type: experience\nmaturity: organized\n---\n\nTwo.",
    });
    const planned = await create({
      title: "Views Project B",
      virtualPath: "views/project-b.md",
      format: "md",
      content:
        "---\nproject: Views Project B\nstatus: planned\n" +
        "type: technical-note\nmaturity: raw\n---\n\nPlanned.",
    });
    const missing = await create({
      title: "Views Missing",
      virtualPath: "views/missing.md",
      format: "md",
      content: "---\nstatus: active\n---\n\nMissing.",
    });
    const blank = await create({
      title: "Views Blank",
      virtualPath: "views/blank.md",
      format: "md",
      content: '---\nproject: "   "\nstatus: active\n---\n\nBlank.',
    });
    return {
      first: first.documentId,
      second: second.documentId,
      planned: planned.documentId,
      missing: missing.documentId,
      blank: blank.documentId,
    };
  });

  await page.goto("/views");
  await expect(page.getByRole("heading", { name: "知识视图" })).toBeVisible();
  const facets = page.locator(".facets-panel");
  const projectA = facets.getByRole("button", {
    name: /Views Project A 2/,
  });
  await expect(projectA).toBeVisible();
  await expect(
    facets.getByRole("button", { name: /Views Project B 1/ }),
  ).toBeVisible();
  await expect(facets.getByRole("button", { name: /未设置/ })).toBeVisible();

  await page.getByRole("switch", { name: "仅显示进行中项目" }).click();
  await expect(page).toHaveURL(/activeOnly=true/);
  await expect(
    facets.getByRole("button", { name: /Views Project B/ }),
  ).toHaveCount(0);

  await facets.getByRole("button", { name: /Views Project A 2/ }).click();
  await expect(page).toHaveURL(/value=VIEWS\+PROJECT\+A/);
  const documentsPanel = page.locator(".documents-panel");
  await expect(
    documentsPanel.getByText("Views Project A One", { exact: true }),
  ).toBeVisible();
  await expect(
    documentsPanel.getByText("Views Project A Two", { exact: true }),
  ).toBeVisible();
  await expect(
    documentsPanel.getByText("Views Project B", { exact: true }),
  ).toHaveCount(0);

  await page.reload();
  await expect(
    documentsPanel.getByText("Views Project A One", { exact: true }),
  ).toBeVisible();

  await page
    .locator(".knowledge-views .n-tabs-tab__label", {
      hasText: /^类型$/,
    })
    .click();
  await expect(page).toHaveURL(/view=types/);
  await expect(
    facets.getByRole("button", { name: /technical-note 2/ }),
  ).toBeVisible();
  await expect(
    facets.getByRole("button", { name: /experience 1/ }),
  ).toBeVisible();

  await page
    .locator(".knowledge-views .n-tabs-tab__label", {
      hasText: /^成熟度$/,
    })
    .click();
  await expect(page).toHaveURL(/view=maturity/);
  await expect(
    facets.getByRole("button", { name: /verified 1/ }),
  ).toBeVisible();
  await expect(
    facets.getByRole("button", { name: /organized 1/ }),
  ).toBeVisible();

  await page.goto("/views?view=projects&value=DOES%20NOT%20EXIST");
  await expect(page.getByText(/分类已不存在/)).toBeVisible();

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/documents");
  await page.getByRole("button", { name: "打开导航" }).click();
  await page.getByRole("button", { name: "知识视图" }).click();
  await expect(page.getByRole("heading", { name: "知识视图" })).toBeVisible();
  await assertNoHorizontalOverflow(page);
  await assertNoSeriousAxeViolations(page, "knowledge views mobile");

  expect(documents).toEqual({
    first: expect.stringMatching(/^[0-9a-f-]+$/),
    second: expect.stringMatching(/^[0-9a-f-]+$/),
    planned: expect.stringMatching(/^[0-9a-f-]+$/),
    missing: expect.stringMatching(/^[0-9a-f-]+$/),
    blank: expect.stringMatching(/^[0-9a-f-]+$/),
  });
});
