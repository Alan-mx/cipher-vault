import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
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
  await page.waitForLoadState("networkidle");
};

const callMcpTool = async (
  page: Page,
  token: string,
  name: string,
  args: Record<string, unknown>,
): Promise<Record<string, unknown>> =>
  page.evaluate(
    async ({ token, name, args }) => {
      const response = await fetch("/mcp", {
        method: "POST",
        headers: {
          Authorization: `Bearer ${token}`,
          "Content-Type": "application/json",
          Accept: "application/json, text/event-stream",
        },
        body: JSON.stringify({
          jsonrpc: "2.0",
          id: 1001,
          method: "tools/call",
          params: { name, arguments: args },
        }),
      });
      const raw = await response.text();
      if (!response.ok) {
        throw new Error(`MCP ${name} failed: ${response.status} ${raw}`);
      }
      const jsonText = raw.slice(raw.indexOf("{"), raw.lastIndexOf("}") + 1);
      const body = JSON.parse(jsonText) as {
        result?: {
          structuredContent?: Record<string, unknown>;
          isError?: boolean;
        };
      };
      if (body.result?.isError || !body.result?.structuredContent) {
        throw new Error(`MCP ${name} returned an invalid result.`);
      }
      return body.result.structuredContent;
    },
    { token, name, args },
  );

const createRelationSuggestion = async (
  page: Page,
  token: string,
  request: {
    relationType: string;
    sourceId: string;
    targetId: string;
    reason: string;
    sourceRevision?: number;
    targetRevision?: number;
  },
): Promise<{ suggestionId: string; suggestionRevision: number }> => {
  const detail = await callMcpTool(page, token, "create_relation_suggestion", {
    relationType: request.relationType,
    sourceDocumentId: request.sourceId,
    sourceDocumentRevision: request.sourceRevision ?? 1,
    targetDocumentId: request.targetId,
    targetDocumentRevision: request.targetRevision ?? 1,
    reason: request.reason,
    modelName: "e2e-model",
    evidence: [],
    idempotencyKey: randomUUID(),
  });
  const metadata = detail.metadata;
  if (
    !metadata ||
    typeof metadata !== "object" ||
    !("suggestionId" in metadata) ||
    typeof metadata.suggestionId !== "string"
  ) {
    throw new Error("MCP create_relation_suggestion returned no suggestionId.");
  }
  return metadata as {
    suggestionId: string;
    suggestionRevision: number;
  };
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
  await expect(
    page.getByText("语义检索暂不可用，已返回关键词结果。"),
  ).toBeVisible();
  await page.getByPlaceholder("输入全文搜索词…").fill("");
  await page
    .locator(".search-mode-row .n-radio__label", { hasText: "关键词" })
    .click();
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
  await expect(page.getByRole("heading", { name: "语义检索" })).toBeVisible();
  await expect(page.getByText("未配置", { exact: true })).toBeVisible();
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
  const mcpToken = await page.locator(".token-value input").inputValue();
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

  const relationSuggestion = await createRelationSuggestion(page, mcpToken, {
    relationType: "related",
    sourceId: relatedDocuments.candidateId,
    targetId: relatedDocuments.directId,
    reason: "E2E confirmed relation.",
  });
  const staleSuggestion = await createRelationSuggestion(page, mcpToken, {
    relationType: "depends_on",
    sourceId: relatedDocuments.candidateId,
    targetId: relatedDocuments.sourceId,
    reason: "E2E stale relation.",
  });
  expect(relationSuggestion.suggestionId).toMatch(/^[0-9a-f-]+$/);
  expect(staleSuggestion.suggestionId).toMatch(/^[0-9a-f-]+$/);

  await page.evaluate(
    async ({ documentId }) => {
      const token = await fetch("/api/vault/antiforgery")
        .then((response) => response.json())
        .then((body: { token: string }) => body.token);
      const response = await fetch(`/api/documents/${documentId}`, {
        method: "PUT",
        headers: {
          "Content-Type": "application/json",
          "X-CSRF-TOKEN": token,
          "If-Match": '"1"',
        },
        body: JSON.stringify({
          title: "Related Source",
          virtualPath: "related/source.md",
          content:
            "---\nproject: CipherVault\n---\n\n[[Related Direct]]\n\nUpdated for stale check.",
          tags: [],
        }),
      });
      if (!response.ok) {
        throw new Error(
          `Stale relation source update failed: ${response.status}`,
        );
      }
    },
    { documentId: relatedDocuments.sourceId },
  );
  const rejectedSuggestion = await createRelationSuggestion(page, mcpToken, {
    relationType: "implements",
    sourceId: relatedDocuments.candidateId,
    targetId: relatedDocuments.sourceId,
    reason: "E2E rejected relation.",
    targetRevision: 2,
  });
  expect(rejectedSuggestion.suggestionId).toMatch(/^[0-9a-f-]+$/);

  await page.goto("/review");
  await page.getByRole("button", { name: "关系建议" }).click();
  const relationSuggestionButton = page
    .locator(".suggestion-list")
    .getByRole("button", { name: /相关 · Related Candidate/ });
  await expect(relationSuggestionButton).toBeVisible();
  await expect(relationSuggestionButton).toContainText("↔");
  await relationSuggestionButton.click();
  await expect(page.getByText("E2E confirmed relation.")).toBeVisible();
  await expect(page.getByText("与 Related Direct 相互关联")).toBeVisible();
  await expect(page.getByText("提交方提供，未经核实")).toBeVisible();
  await assertNoSeriousAxeViolations(page, "relation review desktop");

  const initialApplyResponsePromise = page.waitForResponse(
    (response) =>
      response
        .url()
        .endsWith(
          `/api/relation-suggestions/${relationSuggestion.suggestionId}/apply`,
        ) && response.request().method() === "POST",
  );
  await page.getByRole("button", { name: "应用关系" }).click();
  await page.getByRole("button", { name: "确认" }).click();
  await expect(page.getByText("关系建议已应用。").last()).toBeVisible();
  const initialApplyBody = (await (
    await initialApplyResponsePromise
  ).json()) as { applyResult: string; relationId: string };
  expect(initialApplyBody.applyResult).toBe("inserted");

  const graphAfterApply = (await page.evaluate(
    async ({ documentId }) => {
      const response = await fetch(
        `/api/graph/local?centerDocumentId=${documentId}` +
          "&depth=1&maxNodes=50&maxEdges=100",
      );
      if (!response.ok) {
        throw new Error(`Typed relation graph failed: ${response.status}`);
      }
      return response.json();
    },
    { documentId: relatedDocuments.candidateId },
  )) as {
    edges: Array<{
      edgeKind: string;
      relationType: string;
      relationId: string;
      isDirected: boolean;
    }>;
  };
  expect(graphAfterApply.edges).toEqual(
    expect.arrayContaining([
      expect.objectContaining({
        edgeKind: "typed_relation",
        relationType: "related",
        relationId: initialApplyBody.relationId,
        isDirected: false,
      }),
    ]),
  );

  const duplicateSuggestion = await createRelationSuggestion(page, mcpToken, {
    relationType: "related",
    sourceId: relatedDocuments.candidateId,
    targetId: relatedDocuments.directId,
    reason: "E2E already exists relation.",
  });
  await page.getByRole("button", { name: "刷新关系建议" }).click();
  const duplicateButton = page
    .locator(".suggestion-list")
    .getByRole("button", { name: /相关 · Related Candidate/ });
  await expect(duplicateButton).toBeVisible();
  await duplicateButton.click();
  await expect(page.getByText("E2E already exists relation.")).toBeVisible();
  const duplicateApplyResponsePromise = page.waitForResponse(
    (response) =>
      response
        .url()
        .endsWith(
          `/api/relation-suggestions/${duplicateSuggestion.suggestionId}/apply`,
        ) && response.request().method() === "POST",
  );
  await page.getByRole("button", { name: "应用关系" }).click();
  await page.getByRole("button", { name: "确认" }).click();
  await expect(page.getByText("关系建议已应用。").last()).toBeVisible();
  const duplicateApplyBody = (await (
    await duplicateApplyResponsePromise
  ).json()) as { applyResult: string; relationId: string };
  expect(duplicateApplyBody.applyResult).toBe("already_exists");
  expect(duplicateApplyBody.relationId).toBe(initialApplyBody.relationId);

  const rejectedButton = page
    .locator(".suggestion-list")
    .getByRole("button", { name: /实现 · Related Candidate/ });
  await expect(rejectedButton).toBeVisible();
  await rejectedButton.click();
  await expect(page.getByText("E2E rejected relation.")).toBeVisible();
  await page.getByRole("button", { name: "拒绝", exact: true }).click();
  await page.getByRole("button", { name: "确认" }).click();
  await expect(page.getByText("关系建议已拒绝。")).toBeVisible();

  await page.locator(".relation-filters .n-base-selection").first().click();
  await page.getByText("需要更新", { exact: true }).last().click();
  const staleButton = page
    .locator(".suggestion-list")
    .getByRole("button", { name: /依赖 · Related Candidate/ });
  await expect(staleButton).toBeVisible();
  await staleButton.click();
  await expect(page.getByText("E2E stale relation.")).toBeVisible();
  await expect(page.getByText(/目标文档版本已变化/)).toBeVisible();
  await expect(page.getByRole("button", { name: "应用关系" })).toHaveCount(0);

  await page.goto(`/documents/${relatedDocuments.candidateId}`);
  await page.getByText("相关", { exact: true }).click();
  await expect(
    page
      .locator(".confirmed-relations")
      .getByText("Related Direct", { exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "撤销正式关系" }).click();
  await page.getByRole("button", { name: "确认" }).click();
  await expect(page.getByText("已撤销正式关系。")).toBeVisible();
  await expect(
    page
      .locator(".confirmed-relations")
      .getByText("Related Direct", { exact: true }),
  ).toHaveCount(0);
  const graphAfterRetract = (await page.evaluate(
    async ({ documentId }) => {
      const response = await fetch(
        `/api/graph/local?centerDocumentId=${documentId}` +
          "&depth=1&maxNodes=50&maxEdges=100",
      );
      if (!response.ok) {
        throw new Error(`Retracted relation graph failed: ${response.status}`);
      }
      return response.json();
    },
    { documentId: relatedDocuments.candidateId },
  )) as {
    edges: Array<{ edgeKind: string }>;
  };
  expect(
    graphAfterRetract.edges.filter(
      (edge) => edge.edgeKind === "typed_relation",
    ),
  ).toHaveLength(0);

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
  await expect(page.getByRole("heading", { name: "AI 工作区" })).toBeVisible();
  await page.getByRole("button", { name: /Review Draft/ }).click();
  await expect(page.locator(".draft-detail textarea")).toBeVisible();
  await page.screenshot({
    path: resolve(screenshotRoot, "review-1280.png"),
    fullPage: true,
  });
  await assertNoSeriousAxeViolations(page, "review desktop");
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
  await expect(page.getByRole("heading", { name: "AI 工作区" })).toBeVisible();
  await page.getByRole("button", { name: "关系建议" }).click();
  await expect(
    page.getByRole("region", { name: "关系建议审核" }),
  ).toBeVisible();
  await assertNoHorizontalOverflow(page);
  await assertNoSeriousAxeViolations(page, "relation review mobile");
  await page.getByRole("button", { name: "文档草稿" }).click();
  await page.getByRole("button", { name: /历史/ }).click();
  await page.getByRole("button", { name: /Review Draft/ }).click();
  await expect(page.locator(".detail-heading")).toContainText("已提交");
  await expect(page.getByRole("button", { name: "提交正式文档" })).toHaveCount(
    0,
  );
  await page.screenshot({
    path: resolve(screenshotRoot, "review-390.png"),
    fullPage: true,
  });
  await assertNoSeriousAxeViolations(page, "review mobile");
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
    const text = await create({
      title: "Views Text",
      virtualPath: "views/text.txt",
      format: "txt",
      content: "Plain text.",
    });
    return {
      first: first.documentId,
      second: second.documentId,
      planned: planned.documentId,
      missing: missing.documentId,
      blank: blank.documentId,
      text: text.documentId,
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
  await expect(page).toHaveURL(/mode=maturity-queue/);
  await expect(page).toHaveURL(/bucket=pending_organization/);
  const maturityQueue = page.locator(".maturity-queue");
  const plannedRow = maturityQueue
    .locator(".queue-item")
    .filter({ hasText: "Views Project B" });
  const blankRow = maturityQueue
    .locator(".queue-item")
    .filter({ hasText: "Views Blank" });
  const textRow = maturityQueue
    .locator(".queue-item")
    .filter({ hasText: "Views Text" });
  const confirmMaturityAction = async (): Promise<void> => {
    await page
      .locator(".n-popconfirm:visible")
      .getByRole("button", { name: "确认" })
      .last()
      .click();
  };
  await expect(plannedRow).toBeVisible();
  await expect(blankRow).toBeVisible();
  await expect(textRow).toBeVisible();
  await expect(
    textRow.getByText("TXT 不支持 Frontmatter 成熟度"),
  ).toBeVisible();
  await expect(
    textRow.getByRole("button", { name: "设置成熟度" }),
  ).toBeDisabled();

  await page.route(`**/api/documents/${documents.blank}`, async (route) => {
    if (route.request().method() !== "PATCH") {
      await route.continue();
      return;
    }
    await route.fulfill({
      status: 409,
      contentType: "application/json",
      body: JSON.stringify({
        code: "revision_conflict",
        message: "Document revision changed.",
        details: { currentRevision: 2 },
        correlationId: "maturity-conflict",
      }),
    });
  });
  await blankRow.getByRole("button", { name: "初始化为 原始" }).click();
  await confirmMaturityAction();
  await expect(
    maturityQueue.getByText("文档版本已更新，请刷新队列后重新操作。"),
  ).toBeVisible();
  await page.unroute(`**/api/documents/${documents.blank}`);

  await plannedRow.getByRole("button", { name: "前进到 摘录" }).click();
  await confirmMaturityAction();
  await expect(page.getByText("知识成熟度已更新。")).toBeVisible();
  await expect(plannedRow).toContainText("excerpt");

  await plannedRow.getByRole("button", { name: "设置成熟度" }).click();
  const maturityDialog = page.getByRole("dialog");
  await expect(maturityDialog).toBeVisible();
  await maturityDialog.locator(".n-base-selection").click();
  await page.getByText("verified", { exact: true }).last().click();
  await maturityDialog.getByRole("button", { name: "确认修改" }).click();
  await expect(page.getByText("知识成熟度已更新。").last()).toBeVisible();
  await expect(plannedRow).toHaveCount(0);

  await maturityQueue
    .locator(".n-radio-button")
    .filter({ hasText: "已完成" })
    .click();
  await expect(page).toHaveURL(/bucket=completed/);
  const completedRow = maturityQueue
    .locator(".queue-item")
    .filter({ hasText: "Views Project B" });
  await expect(completedRow).toBeVisible();
  await completedRow.getByRole("button", { name: "清除" }).click();
  await confirmMaturityAction();
  await expect(page.getByText("知识成熟度已更新。").last()).toBeVisible();
  await expect(completedRow).toHaveCount(0);
  await maturityQueue
    .locator(".n-radio-button")
    .filter({ hasText: "待整理" })
    .click();
  await expect(page).toHaveURL(/bucket=pending_organization/);
  await expect(
    maturityQueue.locator(".queue-item").filter({ hasText: "Views Project B" }),
  ).toBeVisible();

  await page
    .locator(".maturity-mode .n-radio-button")
    .filter({ hasText: "分类浏览" })
    .click();
  await expect(page).toHaveURL(/mode=maturity-browse/);
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
  await page.goto(
    "/views?view=maturity&mode=maturity-queue&bucket=pending_organization",
  );
  await expect(
    page.getByRole("region", { name: "知识成熟度整理队列" }),
  ).toBeVisible();
  await assertNoHorizontalOverflow(page);
  await assertNoSeriousAxeViolations(page, "maturity queue mobile");

  expect(documents).toEqual({
    first: expect.stringMatching(/^[0-9a-f-]+$/),
    second: expect.stringMatching(/^[0-9a-f-]+$/),
    planned: expect.stringMatching(/^[0-9a-f-]+$/),
    missing: expect.stringMatching(/^[0-9a-f-]+$/),
    blank: expect.stringMatching(/^[0-9a-f-]+$/),
    text: expect.stringMatching(/^[0-9a-f-]+$/),
  });
});
