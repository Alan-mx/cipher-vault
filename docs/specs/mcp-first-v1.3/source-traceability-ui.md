# Source 定位读取 UI 规格

> 需求名：mcp-first-v1.3-source-traceability ｜ 日期：2026-11-05

## 1. 目标

补齐 Source 详情页对不可变版本、章节/段落定位和定位内容的读取，让用户能够验证 Knowledge 的来源依据，而不是只能看到第一段正文。

## 2. 数据与接口

- 不新增数据库表、Migration 或后端接口。
- 复用：
  - `GET /api/sources/{sourceId}`
  - `GET /api/sources/{sourceId}/revisions`
  - `GET /api/sources/{sourceId}/revisions/{revision}/content`
  - `GET /api/sources/{sourceId}/revisions/{revision}/locators`
  - `GET /api/sources/{sourceId}/revisions/{revision}/locators/{locatorId}/content`
- 所有读取仍要求 Cookie Session 和已解锁 Vault。

## 3. UI

`/sources` 增加：

- 可键盘操作的来源列表。
- 版本选择器。
- 来源正文读取。
- 章节/段落定位列表。
- 点击定位后显示定位区间文本。
- Source Draft 创建入口保持不变。

## 4. 验收标准

- WHEN 用户选择一个 Source THEN 页面加载其版本、当前正文和定位列表。
- WHEN 用户切换 SourceRevision THEN 正文与定位列表随之切换。
- WHEN 用户选择 Locator THEN 页面显示该定位区间的文本。
- WHEN Source 没有 Locator THEN 页面显示空状态，不伪造定位。
- WHEN 使用移动端访问 THEN 列表、版本和正文不重叠且可操作。
- WHEN 页面进行 Axe 检查 THEN 无 serious/critical 问题。

## 5. 非目标

- 不新增 Source 编辑能力。
- 不自动抓取 URL。
- 不新增跨页 Source 搜索。
- 不改变 Source 不可变版本和软删除规则。
