# 数据结构设计（G3-1）

> 需求名：v1.3-baseline-convergence ｜ 日期：2026-11-06 ｜ 状态：已确认
>
> 前置：`spec.md` 已确认。本文只确认数据结构，不进入接口清单和实现。

## 1. 方案概述

本轮不新增数据库表、列、索引或领域实体。数据结构变化只有前端 AI 工作区的状态筛选
联合类型和有序选项表；Linux 截图与文档 Git 白名单作为静态交付清单定义，不进入运行时
存储。

选择“保留现有 Draft 字符串合同 + 前端白名单收窄”的方案，因为后端已经接受全部 7 种
Draft 状态。备选方案是新增前端全局 `DraftStatus` 类型或修改后端枚举映射，但两者都会
扩大公共类型和接口影响面，不采用。

## 2. 复用清单

| 想要的能力 | 现有实现 | 复用方式 |
|---|---|---|
| 查询历史终态 | `GET /api/drafts?status=...`；`KnowledgeBaseValueMapping.ParseDraftStatus` | 直接复用准确状态值，不扩展参数 |
| 筛选 Draft 列表 | `knowledgeBaseApi.drafts({ status, pageSize })` | 直接调用现有 API client |
| 历史详情只读 | `UnifiedReviewView.vue` 的 `canEdit` 和状态条件按钮 | 保留现有只读判定，不新增写操作 |
| URL 状态恢复 | `route.query.status` 与 `syncQuery()` | 扩展允许值并统一解析 |
| 视觉回归 | `p0.spec.ts` 的 `toHaveScreenshot("knowledge-library.png")` | 新增 Linux 平台快照，不改断言 |
| P0 证据 | `docs/specs/mcp-first-v1.3/` | 作为指定文档白名单直接纳入 |

## 3. 数据库与领域模型

### 3.1 DDL 草案

```sql
-- 无 DDL。
-- Schema 保持 1，Migration 仍为 001_mcp_first_baseline.sql。
-- Backup FormatVersion 保持 1。
```

### 3.2 不新增/不修改的数据结构

- 不修改 `DraftStatus` 枚举；继续使用 `Draft`、`PendingReview`、`Conflict`、
  `Applied`、`Rejected`、`Discarded`、`Expired`。
- 不修改 `drafts` 表、`audit_events`、`idempotency_records` 或现有索引。
- 不修改 `KnowledgeStatus`；仍只有 `Active`、`Archived`。
- 不新增成熟度表、属性种子或生命周期字段。
- 不修改 Knowledge Triage 的请求、响应或持久化状态。

## 4. 前端筛选数据模型

### 4.1 文件归属

唯一运行时数据结构修改点：

```text
v2/frontend/src/views/UnifiedReviewView.vue
```

不新增共享类型文件。状态选项是页面级 UI 数据，直接依赖后端现有字符串合同，避免制造
一套与领域枚举重复的公共抽象。

### 4.2 状态筛选类型

页面内定义有序常量，并由常量推导联合类型：

```ts
const statusOptions = [
  { value: "pending_review", label: "待审核" },
  { value: "draft", label: "处理中" },
  { value: "conflict", label: "冲突" },
  { value: "applied", label: "已应用" },
  { value: "rejected", label: "已拒绝" },
  { value: "discarded", label: "已丢弃" },
  { value: "expired", label: "已过期" },
] as const;

type ReviewStatus = (typeof statusOptions)[number]["value"];
```

约束：

- 顺序固定为上述顺序，保证下拉选项和测试稳定。
- `status` ref 的类型为 `ReviewStatus`。
- `parseReviewStatus(value: unknown): ReviewStatus` 只接受七个值，其他输入返回
  `pending_review`。
- 不使用 `as ReviewStatus` 静默接受未知值。
- 不新增“history”聚合值；四个终态分别查询。

### 4.3 值映射

| wire value | UI 标签 | 分类 | 是否可编辑/审核 |
|---|---|---|---|
| `pending_review` | 待审核 | 进行中 | 沿用现有行为 |
| `draft` | 处理中 | 进行中 | 沿用现有行为 |
| `conflict` | 冲突 | 进行中 | 沿用现有行为 |
| `applied` | 已应用 | 历史终态 | 只读 |
| `rejected` | 已拒绝 | 历史终态 | 只读 |
| `discarded` | 已丢弃 | 历史终态 | 只读 |
| `expired` | 已过期 | 历史终态 | 只读 |

该表只是前端展示和路由解析口径，不新增 API 字段。

### 4.4 URL 与 API 传递

| 位置 | 类型 | 值来源 | 约束 |
|---|---|---|---|
| `route.query.status` | `unknown` | URL | 使用 `parseReviewStatus` 收窄 |
| `status` ref | `ReviewStatus` | route/UI | 默认 `pending_review` |
| `knowledgeBaseApi.drafts` 入参 | `string` | 当前 `status` | 原样发送 wire value |
| `router.replace.query.status` | `ReviewStatus` | 当前筛选 | 始终写允许值 |

不修改 `DraftQuery`、`DraftListItem.status`、`DraftRecord.status` 的现有字符串类型。

## 5. 测试数据模型

### 5.1 前端单元测试

复用 `v2/frontend/tests/unit/draft-review-editing.spec.ts`，增加以下逻辑测试数据：

- 状态选项集合：7 个 wire value 与 7 个中文标签。
- 路由输入：4 个合法终态分别触发对应 `status` 查询。
- 非法输入：未知状态回退 `pending_review`。
- 终态详情：使用现有 `draftDetail(status)` 夹具，逐终态断言不出现提交、保存、应用、
  拒绝或丢弃按钮。
- 空列表：四个终态均返回空 items 时，页面仍为可用空队列，不显示错误。
- `status` 与 `draftId` 同时存在：若有效详情状态与 URL 状态不同，以详情状态为准，
  重新加载对应状态列表并同步 URL。

测试夹具不需要数据库或新增后端测试数据。

### 5.2 Linux 截图基准

| 属性 | 值 |
|---|---|
| 文件 | `v2/frontend/tests/e2e/p0.spec.ts-snapshots/knowledge-library-linux.png` |
| 格式 | PNG |
| 视口 | 1280 × 720，与现有 E2E 默认视口一致 |
| 生成环境 | Playwright Chromium / Ubuntu CI |
| Windows 基准 | `knowledge-library-win32.png` 保持原文件不变；当前 SHA256 为 `530918FFC0B64CB78498C1AAA1BDB49CE3DBEB8190A9D6D320698C526D74A9D4` |
| 断言 | 继续使用 `toHaveScreenshot("knowledge-library.png")` |

Linux PNG 不写入数据库，不作为运行时资源，只作为测试基线提交。

## 6. 文档交付清单

本轮 Git 文档集合固定为：

```text
docs/CipherVault v1.3 产品与知识架构设计.md
docs/specs/mcp-first-v1.3/audit.md
docs/specs/mcp-first-v1.3/clarify.md
docs/specs/mcp-first-v1.3/design-data.md
docs/specs/mcp-first-v1.3/design-interfaces.md
docs/specs/mcp-first-v1.3/knowledge-model-ui.md
docs/specs/mcp-first-v1.3/mcp-e2e-validation.md
docs/specs/mcp-first-v1.3/mcp-navigation.md
docs/specs/mcp-first-v1.3/progress.md
docs/specs/mcp-first-v1.3/prompts.md
docs/specs/mcp-first-v1.3/source-traceability-ui.md
docs/specs/mcp-first-v1.3/spec.md
docs/specs/v1.3-baseline-convergence/clarify.md
docs/specs/v1.3-baseline-convergence/spec.md
docs/specs/v1.3-baseline-convergence/design-data.md
docs/specs/v1.3-baseline-convergence/design-interfaces.md
docs/specs/v1.3-baseline-convergence/progress.md
docs/specs/v1.3-baseline-convergence/verification.md
```

约束：

- 不使用 `git add docs/` 或递归暂存整个目录。
- 不加入 `docs/architecture/`、`docs/bugs/` 或其他 `docs/specs/` 内容。
- 新增文件必须在完成时真实存在；未生成的 `progress.md`、`verification.md` 在对应阶段创建。

## 7. 数据不变量

- `statusOptions` 的 wire value 必须与现有 Draft API 的七种值完全一致。
- `statusOptions` 不允许重复值，顺序固定。
- 当前筛选只能是单一状态，不允许逗号拼接、数组或多状态 OR。
- 当前列表筛选必须与已打开 Draft 的实际状态一致，不允许长期保留不一致状态。
- 历史终态不改变服务端状态，也不产生 Audit 记录。
- 页面筛选请求只使用 `pageSize: 100` 的现有行为，本轮不调整分页。
- Linux 和 Windows 截图是两份独立平台基准，不能互相覆盖。
- 文档 Git 白名单是显式文件清单，不扩展为目录级通配。

## 8. 迁移、兼容与回滚

- 数据库迁移：无。
- 数据回填：无。
- 后端兼容：无需变更。
- 前端回滚：恢复 `UnifiedReviewView.vue` 的状态列表和解析逻辑即可。
- 截图回滚：删除新 Linux PNG，保留 Windows PNG；回滚后 Linux E2E 将重新缺少基准。
- 文档回滚：按白名单撤销具体文件，不清理整个本地 `docs/`。

## 9. AC 承接

| AC | 数据结构承接 |
|---|---|
| AC13-AC18 | `statusOptions`、`ReviewStatus`、`parseReviewStatus` 和现有 API 状态值 |
| AC19 | 现有 `canEdit` 与状态条件操作按钮保持不变 |
| AC20-AC21 | URL parser 只接受七值，非法值回退 `pending_review` |
| AC22-AC23 | 不做多状态聚合，不修改 API 数据类型 |
| AC25-AC27 | 双平台截图文件名与现有断言路径 |
| AC30-AC34 | 明确不修改领域枚举、Triage、Schema 和 MCP |

## 10. G3-1 确认结果

2026-11-06 用户确认本数据结构设计，包括：

- 不新增共享 Draft 状态类型，只在 `UnifiedReviewView.vue` 内定义页面级联合类型。
- 七种状态使用扁平下拉列表，不增加“历史”分组或跨状态聚合查询。
- Linux 截图固定为 1280 × 720 PNG，Windows 基准原样保留。
- 采用显式文档 Git 白名单。

接口与任务清单见 `design-interfaces.md`。
