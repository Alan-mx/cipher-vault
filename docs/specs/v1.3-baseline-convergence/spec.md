# 规格（G2）

> 需求名：v1.3-baseline-convergence ｜ 日期：2026-11-06 ｜ 状态：已确认
>
> 前置：`clarify.md` 已由用户确认；代码基线
> `4afeecce24ffda4c804a5926bf28d3ba00650ece`。

## 1. 背景与目标

v1.3 P0 主体实现已经通过验收，但仓库中的产品设计、P0 实施证据和当前代码存在少量
口径漂移；AI 工作区没有暴露后端已经支持的 rejected、discarded、expired 历史状态；
Linux CI 缺少 Playwright 截图基准。

本轮只做基线收敛，不扩展产品能力：

- 统一 v1.3 产品设计、P0 验收证据和本轮收敛文档。
- 在 AI 工作区补齐四种终态历史筛选。
- 在 Linux CI 环境生成正式截图基准，保留现有视觉断言。
- 明确本轮不修改 Schema、Migration、MCP 契约或整理队列行为。
- 只把指定权威文档纳入 Git，不提交整个本地 `docs/` 目录。

## 2. 用户故事

- 作为后续实现者，我要看到与当前代码一致的 v1.3 产品合同和验收证据，以便不再依据
  已收敛的旧建议开发。
- 作为知识库用户，我要在 AI 工作区按四种终态查看历史 Draft，以便回溯已应用、已拒绝、
  已丢弃和已过期的审核结果。
- 作为维护者，我要 Linux CI 使用正式截图基准通过 E2E，以便后续提交能获得完整平台
  验证，而不是被已知快照缺失阻塞。

## 3. 验收标准（EARS）

### 3.1 收敛边界与产品口径

- AC1: WHEN 本轮实施 THE SYSTEM SHALL 以
  `4afeecce24ffda4c804a5926bf28d3ba00650ece` 为代码基线记录收敛对象。
- AC2: WHEN 描述知识成熟度 THE SYSTEM SHALL 明确它不是固定系统生命周期；如需成熟度，
  只能通过 Knowledge Model 自定义属性表达。
- AC3: WHEN 描述 P0 整理队列 THE SYSTEM SHALL 只列出 `missing_required_property`、
  `missing_source`、`stale` 三类知识规则，并单独描述 Draft/Conflict 待办计数。
- AC4: WHEN 描述整理队列操作 THE SYSTEM SHALL 明确当前只读，只提供查看详情和复制
  AI 整理任务，不提供直接整理、暂时保留、忽略、删除或归档操作。
- AC5: WHEN 描述尚未纳入 P0 的整理能力 THE SYSTEM SHALL 把重复检测和观点验证明确标为
  当前范围外，不得表述为已交付能力。
- AC6: WHEN 描述 AI 工作区历史 THE SYSTEM SHALL 列出 `applied`、`rejected`、
  `discarded`、`expired` 四种终态。
- AC7: WHEN 描述复盘或综合的 MCP 路径 THE SYSTEM SHALL 明确使用现有 Prompt、
  读取/搜索、通用 Draft 和提交审核，不声明存在专用 MCP Tool。
- AC8: WHEN 本轮更新 P0 验收记录 THE SYSTEM SHALL 记录 convergence 的结果、验证证据和
  对应文档路径。

### 3.2 文档范围与 Git 白名单

- AC9: WHEN 将文档纳入 Git THE SYSTEM SHALL 只允许以下集合：
  - `docs/CipherVault v1.3 产品与知识架构设计.md`
  - `docs/specs/mcp-first-v1.3/` 下的现有 P0 审计、澄清、规格、设计和验收文档
  - `docs/specs/v1.3-baseline-convergence/clarify.md`
  - `docs/specs/v1.3-baseline-convergence/spec.md`
  - `docs/specs/v1.3-baseline-convergence/design-data.md`
  - `docs/specs/v1.3-baseline-convergence/design-interfaces.md`
  - 本轮生成的 `docs/specs/v1.3-baseline-convergence/progress.md`
  - 本轮生成的 `docs/specs/v1.3-baseline-convergence/verification.md`
- AC10: WHEN 执行 Git 暂存 THE SYSTEM SHALL NOT 使用整个 `docs/` 目录作为暂存范围。
- AC11: WHEN convergence 完成 THE SYSTEM SHALL NOT 把其他 `docs/specs/`、
  `docs/bugs/` 或 `docs/architecture/` 工作产物纳入本次 Git 变更。
- AC12: WHEN `AGENTS.md` 引用产品设计或 P0 证据路径 THE SYSTEM SHALL 保证被引用文件
  存在于本轮 Git 白名单中。

### 3.3 AI 工作区历史筛选

- AC13: WHEN 已解锁 UserUi 打开 `/review` 且未指定状态 THE SYSTEM SHALL 默认查询
  `pending_review`。
- AC14: WHEN 渲染草稿状态筛选 THE SYSTEM SHALL 提供 `pending_review`、`draft`、
  `conflict`、`applied`、`rejected`、`discarded`、`expired` 七个可选值。
- AC15: WHEN 用户选择 `applied` THE SYSTEM SHALL 只查询并展示状态为 `applied` 的 Draft。
- AC16: WHEN 用户选择 `rejected` THE SYSTEM SHALL 只查询并展示状态为 `rejected` 的 Draft。
- AC17: WHEN 用户选择 `discarded` THE SYSTEM SHALL 只查询并展示状态为 `discarded` 的 Draft。
- AC18: WHEN 用户选择 `expired` THE SYSTEM SHALL 只查询并展示状态为 `expired` 的 Draft。
- AC19: WHEN 用户查看任一历史终态 THE SYSTEM SHALL 只读展示详情，不提供提交、编辑、
  应用、拒绝或丢弃操作。
- AC20: WHEN URL 包含 `status=applied|rejected|discarded|expired` THE SYSTEM SHALL 恢复
  对应筛选状态，保持深链可复现。
- AC21: WHEN URL 的 `status` 不属于七个允许值 THE SYSTEM SHALL 回退到 `pending_review`。
- AC22: WHEN 使用现有 Draft 列表 API 查询终态 THE SYSTEM SHALL 分别发送准确的 `status`
  值，不新增组合状态、模糊匹配或客户端全量过滤。
- AC23: WHEN 本轮修改 AI 工作区筛选 THE SYSTEM SHALL NOT 修改 `/api/drafts` 的请求、
  响应、错误码、权限或分页合同。
- AC24: WHEN 终态列表为空 THE SYSTEM SHALL 保持页面可用并显示现有空队列状态，不显示
  错误或将空结果解释为接口失败。

### 3.4 Linux Playwright 基准

- AC25: WHEN 前端 E2E 在 Linux 运行 THE SYSTEM SHALL 能解析到正式基准
  `v2/frontend/tests/e2e/p0.spec.ts-snapshots/knowledge-library-linux.png`。
- AC26: WHEN 新增 Linux 基准 THE SYSTEM SHALL 保留现有
  `knowledge-library-win32.png`，不得用 Linux 图片覆盖 Windows 基准。
- AC27: WHEN 执行 P0 E2E THE SYSTEM SHALL 保留
  `expect(page).toHaveScreenshot("knowledge-library.png")` 视觉断言。
- AC28: WHEN 在 Ubuntu CI 执行完整前端 E2E THE SYSTEM SHALL 通过全部用例且无缺失截图
  基准错误。
- AC29: IF 本轮未取得 Linux E2E 通过证据 THEN THE SYSTEM SHALL 将验收标记为未完成，
  不得用 Windows 截图或 Windows E2E 结果替代 Linux 结论。

### 3.5 非回归与安全边界

- AC30: WHEN 本轮实施 THE SYSTEM SHALL NOT 修改 `KnowledgeStatus`，不得新增固定
  Raw/Excerpt/Organized/Verified/Practiced 枚举或默认种子。
- AC31: WHEN 本轮实施 THE SYSTEM SHALL NOT 修改 `KnowledgeTriageStore`、
  `KnowledgeTriageContracts`、Triage API 或 `TriageView` 的业务行为。
- AC32: WHEN 本轮实施 THE SYSTEM SHALL NOT 修改 DatabaseMigrationCatalog、Migration
  SQL、Schema Version、备份 FormatVersion 或现有表结构。
- AC33: WHEN 本轮实施 THE SYSTEM SHALL NOT 新增、删除或修改 MCP Tool、Resource、
  Prompt、Instructions 或 MCP 权限边界。
- AC34: WHEN 本轮实施 THE SYSTEM SHALL NOT 新增复盘/综合专用 MCP Tool，也不新增
  Direct Write、Agent Runtime 或聊天历史。
- AC35: WHEN 完成实现 THE SYSTEM SHALL 通过相关后端构建与测试、前端 format、
  typecheck、lint、unit test、build 和 E2E。
- AC36: WHEN 汇总验收 THE SYSTEM SHALL 分别报告本地 Windows 验证和 Linux CI/E2E
  验证，不得混写为同一平台结论。

## 4. 字段口径

### 4.1 Draft 历史筛选

| 字段 | 类型 | 字典/枚举 | 必填 | 默认 | 说明 |
|---|---|---|---|---|---|
| `status` | string | `pending_review` / `draft` / `conflict` / `applied` / `rejected` / `discarded` / `expired` | 否 | `pending_review` | 直接映射到现有 Draft 列表 API |
| 历史标签 `applied` | string | `已应用` | 否 | - | 正式变更已应用 |
| 历史标签 `rejected` | string | `已拒绝` | 否 | - | 审核用户拒绝 |
| 历史标签 `discarded` | string | `已丢弃` | 否 | - | 发起方或用户丢弃 |
| 历史标签 `expired` | string | `已过期` | 否 | - | 现有过期生命周期产生 |

### 4.2 文档与截图路径

| 类别 | 路径 | 说明 |
|---|---|---|
| 产品设计 | `docs/CipherVault v1.3 产品与知识架构设计.md` | v1.3 产品与技术准绳 |
| P0 规格与证据 | `docs/specs/mcp-first-v1.3/` | 目录内现有文档全部属于指定 P0 证据集 |
| 本轮 G1 | `docs/specs/v1.3-baseline-convergence/clarify.md` | 已确认的八项边界 |
| 本轮 G2 | `docs/specs/v1.3-baseline-convergence/spec.md` | 本规格 |
| 本轮台账 | `docs/specs/v1.3-baseline-convergence/progress.md` | G4 创建 |
| 本轮验收 | `docs/specs/v1.3-baseline-convergence/verification.md` | G5 创建 |
| Linux 截图 | `v2/frontend/tests/e2e/p0.spec.ts-snapshots/knowledge-library-linux.png` | 正式 Linux 基准 |
| Windows 截图 | `v2/frontend/tests/e2e/p0.spec.ts-snapshots/knowledge-library-win32.png` | 保持现状 |

## 5. 状态流转

### 5.1 页面筛选

| 当前状态 | 触发 | 目标状态 |
|---|---|---|
| 待审核 | 打开 `/review` 且无 status | 查询 `pending_review` |
| 任意筛选 | 选择 `applied` | 查询 `applied` |
| 任意筛选 | 选择 `rejected` | 查询 `rejected` |
| 任意筛选 | 选择 `discarded` | 查询 `discarded` |
| 任意筛选 | 选择 `expired` | 查询 `expired` |
| 任意筛选 | 从 URL 打开允许状态 | 查询对应状态并同步筛选控件 |
| 任意筛选 | URL 含非法状态 | 查询 `pending_review` |
| 终态筛选 | 选中列表项 | 只读详情 |

本轮不改变 Draft 自身状态，不增加新的 Draft 状态流转。

### 5.2 文档与验收

| 当前状态 | 触发 | 目标状态 |
|---|---|---|
| G1 已确认 | 生成 G2 | spec 待确认 |
| G2 待确认 | 用户确认 | G3 设计 |
| G4 完成 | 执行收敛验证 | verification 记录 |
| Windows 已验证、Linux 未验证 | 汇总 | 结论保持 partial，不得宣称完成 |
| Linux CI E2E 通过 | 汇总 | 可给出 Linux verified 结论 |

## 6. 权限矩阵

| 操作 | UserUi | Mcp | System |
|---|---|---|---|
| 查看七种 Draft 筛选及详情 | 允许，沿用现有 VaultSession | 不提供 | 不提供 |
| 提交/编辑/应用/拒绝/丢弃 Draft | 沿用现有权限，不因筛选改变 | 沿用现有 Draft-only 边界 | 不提供 |
| 查看文档与验收证据 | 本地仓库读者 | 不通过 MCP 暴露 | 不提供 |
| 生成或修改 Linux 基准 | 维护者 | 不提供 | CI 环境 |

## 7. 非目标

- 不扩展整理队列六类规则。
- 不新增整理队列逐项操作或持久化忽略/保留状态。
- 不增加固定知识成熟度生命周期或成熟度 Migration。
- 不新增跨状态聚合查询参数，不修改 Draft 列表 API。
- 不内联 Audit 明细到 Draft 历史页面。
- 不新增复盘/综合专用 MCP Tool。
- 不新增 Schema、Migration、表、列、索引或后台任务。
- 不修改 MCP Tools、Resources、Prompts、Instructions、Token 或权限模型。
- 不提交整个 `docs/` 目录。
- 不调整截图断言、视口、主题或容忍度来绕开 Linux 差异。

## 8. 影响面

- 涉及模块：
  - `docs/CipherVault v1.3 产品与知识架构设计.md`
  - `docs/specs/mcp-first-v1.3/`
  - `docs/specs/v1.3-baseline-convergence/`
  - `v2/frontend/src/views/UnifiedReviewView.vue`
  - `v2/frontend/tests/e2e/p0.spec.ts-snapshots/`
  - 相关前端单元/E2E 测试
- 涉及表：只读现有 `drafts` 数据；无结构变更。
- Schema/Migration：无变化。
- MCP：无变化。
- 备份/恢复：无变化。
- 关联端：当前 Windows/Linux 单进程 Server 与 Vue Web UI；无镜像仓库。
- 验证重点：文档口径、七种筛选、终态只读、深链、Linux 截图和 CI 完整通过。
