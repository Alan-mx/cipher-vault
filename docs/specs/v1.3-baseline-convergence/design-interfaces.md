# 接口与任务设计（G3-2）

> 需求名：v1.3-baseline-convergence ｜ 日期：2026-11-06 ｜ 状态：待确认
>
> 前置：`design-data.md` 已确认。本文只确认接口和任务，不开始实现。

## 1. 方案概述

本轮不新增或修改 HTTP/MCP 接口。现有 `GET /api/drafts` 与
`GET /api/drafts/{draftId}` 直接复用，只扩展 `/review` 页面的状态参数允许值。
交付面包括一个前端页面接口、一个 Linux 截图基准文件和一个显式文档 Git 清单。

备选方案是新增“历史聚合”后端参数，同时查询多个终态；该方案需要修改请求合同、查询实现
和测试，但用户已确认按终态分别筛选，因此不采用。

## 2. 复用清单

| 想要的能力 | 现有实现 | 复用方式 |
|---|---|---|
| 按状态取 Draft 列表 | `GET /api/drafts`；`UnifiedDraftEndpoints.ParseStatus` | 直接调用，七个状态值均已支持 |
| 读取 Draft 详情 | `GET /api/drafts/{draftId}`；`knowledgeBaseApi.draft` | 直接调用 |
| 前端 Draft client | `knowledgeBaseApi.drafts` | 原样传递 `status` 和 `pageSize` |
| URL 筛选恢复 | `useRoute`、`router.replace`、`syncQuery` | 扩展解析和选项 |
| 终态只读 | `canEdit` 与模板状态条件 | 保持现有逻辑，不增加动作 |
| 视觉断言 | `p0.spec.ts` | 不修改断言，仅补 Linux 平台基准 |

## 3. 第一步：数据结构确认结果

- DDL：无。
- 数据库迁移：无。
- 领域模型：无。
- 页面级结构：`statusOptions`、`ReviewStatus`、`parseReviewStatus`，详见
  `design-data.md`。

## 4. 第二步：接口清单

### I1 `/review?status=...` 页面筛选接口

| 项目 | 内容 |
|---|---|
| 类型 | Vue 页面路由/UI 合同 |
| 方法 | 页面导航 |
| 路径 | `/review?status=<ReviewStatus>` |
| 职责 | 按单一 Draft 状态筛选并展示列表与详情 |
| 入参 | `status`：七个允许值，可缺省；`draftId`：可选现有参数 |
| 出参 | 对应状态列表、计数、详情和只读/操作状态 |
| 变更 | 扩展允许值，复用现有请求 |
| 合同变化 | 无 HTTP API 变化 |

细节：

- 缺省或非法 `status` 归一为 `pending_review`。
- 七个值为 `pending_review`、`draft`、`conflict`、`applied`、`rejected`、
  `discarded`、`expired`。
- 选择状态后调用现有 `knowledgeBaseApi.drafts({ status, pageSize: 100 })`。
- URL 同步后保留现有 `draftId` 处理方式；切换状态时删除旧 `draftId`。
- 若 URL 同时提供 `status` 与有效 `draftId`，以详情实际状态为准；同步筛选并重新加载
  对应状态列表，避免详情状态与列表筛选不一致。
- 历史终态只读，现有 Draft 动作不显示。

### I2 Linux Playwright 截图文件

| 项目 | 内容 |
|---|---|
| 类型 | 静态测试基准 |
| 路径 | `v2/frontend/tests/e2e/p0.spec.ts-snapshots/knowledge-library-linux.png` |
| 职责 | 作为 Linux Chromium P0 知识库页视觉回归基准 |
| 入参 | Playwright 测试生成的实际截图 |
| 出参 | 图片比较通过或失败 |
| 变更 | 新增文件 |
| 合同变化 | 不修改断言名称、视口或阈值 |

### I3 v1.3 文档内容合同

| 项目 | 内容 |
|---|---|
| 类型 | 仓库交付合同 |
| 路径 | `docs/CipherVault v1.3 产品与知识架构设计.md`、`docs/specs/mcp-first-v1.3/`、`docs/specs/v1.3-baseline-convergence/` |
| 职责 | 让产品设计、P0 证据和收敛文档表达同一当前基线 |
| 入参 | 已确认的 spec 和现有 P0 文档 |
| 出参 | 口径一致、路径可解析的权威文档 |
| 变更 | 更新过期内容与状态说明 |
| 合同变化 | 不扩展产品或技术范围 |

### I4 文档 Git 白名单合同

| 项目 | 内容 |
|---|---|
| 类型 | Git 暂存范围合同 |
| 路径 | 显式文件清单，见 `design-data.md` 第 6 节 |
| 职责 | 只将指定文件纳入 Git 跟踪 |
| 入参 | I3 输出的已存在文档 |
| 出参 | 与白名单完全一致的暂存文件集 |
| 变更 | 新增指定文件到 Git 跟踪范围 |
| 合同变化 | 不使用整个 `docs/` 目录通配 |

## 5. 不修改的接口

| 接口 | 当前合同 | 本轮处理 |
|---|---|---|
| `GET /api/drafts` | `status`、`targetObjectType`、`operationType`、`sourceActor`、`targetId`、时间、分页、cursor | 原样复用，不修改 |
| `GET /api/drafts/{draftId}` | 返回现有 DraftDetail | 原样复用 |
| Draft action APIs | submit/discard/reject/apply | 原样复用，不因历史筛选改变 |
| MCP Tools/Resources/Prompts | 当前 Draft-only 合同 | 完全不变 |

## 6. 实现任务清单

### T1 扩展 AI 工作区历史筛选

文件：

- `v2/frontend/src/views/UnifiedReviewView.vue`
- `v2/frontend/tests/unit/draft-review-editing.spec.ts`

动作：

- 用 `statusOptions` 替代当前 `allowedStatuses` 数组和手写 `<option>`。
- 增加 `ReviewStatus` 联合类型、`isReviewStatus` 和 `parseReviewStatus`。
- 初始化 `status` ref 时统一走 parser。
- `openById` 只在详情状态为允许值时更新筛选，否则保留当前筛选。
- 初始深链的有效 `draftId` 状态与 URL 状态不同时，详情状态优先；同步 URL 后重新加载
  对应状态列表，并保留刚读取的详情。
- 下拉列表使用 `v-for="option in statusOptions"`，七个标签按设计固定顺序渲染。
- 保持现有 `load()`、`syncQuery()`、分页大小、详情读取和动作按钮逻辑。
- 保留侧栏现有 `aria-label="待审核草稿"`，避免无需求依据地改动四个 E2E 文件共用的
  无障碍名称。
- 增加单元测试：七项选项、四个终态查询、四个终态空列表、非法值回退、终态只读、
  默认待审核和 `draftId` 状态优先。

完成门槛：

- `npm test -- draft-review-editing` 通过。
- `npm run typecheck` 通过。
- `npm run lint` 通过。
- 不修改 `knowledgeBaseApi`、`types/knowledge-base.ts` 或任何服务端文件。

### T2 增加 Linux 截图基准

文件：

- `v2/frontend/tests/e2e/p0.spec.ts-snapshots/knowledge-library-linux.png`
- 必要时仅更新该平台 PNG；`knowledge-library-win32.png` 必须保持原 SHA256。

动作：

- 在 Linux Chromium 环境生成 `knowledge-library.png` 的平台截图。
- 确认生成文件名为 `knowledge-library-linux.png`、尺寸为 1280 × 720。
- 不修改 `p0.spec.ts` 的截图名称、视口或阈值。
- 在 P0 辅助流程中等待 `/apply` 返回成功且审批按钮消失后再导航；该等待只修复
  Linux 下异步请求被页面跳转取消的测试竞态，不改变业务或截图断言。
- 普通 CI 不会自动生成缺失基准；先生成并提交 PNG，再由后续 Linux E2E/CI 使用该文件
  完成比较验证。

完成门槛：

- Linux 定向 P0 E2E 通过。
- Ubuntu CI 完整前端 E2E 通过。
- Windows 基准 SHA256 仍为
  `530918FFC0B64CB78498C1AAA1BDB49CE3DBEB8190A9D6D320698C526D74A9D4`。

### T3 收敛产品与验收文档

文件：

- `docs/CipherVault v1.3 产品与知识架构设计.md`
- `docs/specs/mcp-first-v1.3/progress.md`
- `docs/specs/v1.3-baseline-convergence/clarify.md`
- `docs/specs/v1.3-baseline-convergence/spec.md`
- `docs/specs/v1.3-baseline-convergence/design-data.md`
- `docs/specs/v1.3-baseline-convergence/design-interfaces.md`

动作：

- 产品设计顶部记录代码基线、核对日期和已验证/待验证边界。
- 6.1 删除固定五阶段成熟度建议，改为 Knowledge Model 自定义属性说明。
- 6.2 只保留三类整理规则和 Draft/Conflict 独立计数，标明队列只读；重复检测与观点
  验证明确标为当前范围外。
- 8.2 更新 AI 工作区历史为 applied/rejected/discarded/expired 四种终态。
- 10.1 更新实际完成状态；MCP 复盘/综合继续描述为 Prompt + 通用 Draft。
- 删除产品设计顶部对未纳入 Git 的
  `docs/architecture/cipher-vault-refactor-v1.2.md` 的依赖路径；如保留历史沿革说明，
  必须明确它不是当前合同来源且不要求文件存在。
- P0 `progress.md` 增加 convergence 条目，记录后端、前端、Windows E2E 和 Linux CI 证据。
- G4 后续创建 `progress.md`；G5 后续创建 `verification.md`。

完成门槛：

- 文档中不再把五阶段成熟度写成系统默认能力。
- 文档中不再把六类整理规则或逐项整理操作写成已交付 P0。
- 文档中 AI 工作区历史包含四种终态。
- 所有新增相对路径真实存在。

### T4 文档 Git 白名单落地

文件：

- `design-data.md` 第 6 节列出的全部文档。

动作：

- 只按显式文件清单执行 Git 暂存。
- 不执行 `git add docs/`。
- 不加入其他 `docs/specs/`、`docs/bugs/`、`docs/architecture/`。

完成门槛：

- `git status --short` 中本次文档变更与白名单完全一致。
- `AGENTS.md` 引用的产品设计和 `mcp-first-v1.3` 路径均存在。

### T5 非回归与收敛验证

文件：

- 不新增业务文件。

动作：

- 对照 AC1-AC36 逐条重验。
- 确认未修改 Triage、KnowledgeStatus、Migration、MCP 注册和备份合同。
- 运行仓库约定的后端、前端和 E2E 验证。
- 将 Windows 本地结果与 Linux CI/E2E 结果分开记录。

完成门槛：

- AC1-AC36 全部 verified。
- Linux E2E 未通过时结论保持 partial，禁止进入 G6。

## 7. 接口与任务对应

| 接口 | 对应任务 | 说明 |
|---|---|---|
| I1 `/review?status=...` | T1 | 扩展页面接口和测试 |
| I2 Linux 截图文件 | T2 | 新增正式基准并验证 |
| I3 v1.3 文档内容合同 | T3 | 文档内容收敛 |
| I4 文档 Git 白名单合同 | T4 | 只暂存指定文件 |
| 无接口变化项 | T5 | 非回归和 AC 全量验证 |

## 8. AC 对应关系

| AC | 承接任务 |
|---|---|
| AC1 | T3 |
| AC2-AC7 | T3 |
| AC8 | T3、T5 |
| AC9-AC12 | T3、T4 |
| AC13-AC14 | T1 |
| AC15-AC19 | T1 |
| AC20-AC21 | T1 |
| AC22 | T1 |
| AC23 | T1、T5 |
| AC24 | T1 |
| AC25-AC27 | T2 |
| AC28-AC29 | T2、T5 |
| AC30-AC34 | T5 |
| AC35-AC36 | T5 |

无未承接 AC。

## 9. 消息 / 通知影响

无。

## 10. 关联端 / 镜像仓库同步影响

无。只涉及当前 Windows/Linux 单进程 Server 与 Vue Web UI；不涉及镜像仓库、移动端或
其他客户端。

## 11. 风险与回滚

- 风险：Linux Chromium 的字体和抗锯齿与 Windows 不同，不能用 Windows 截图推导 Linux
  基准；必须由 Linux 环境生成。
- 风险：历史终态筛选若只改下拉而未统一 URL parser，会产生刷新后状态丢失；由 T1 单元
  测试覆盖。
- 风险：整个 `docs/` 未跟踪，误用目录级暂存会把大量非本轮产物带入 Git；T4 只允许显式
  文件清单。
- 回滚：前端恢复 `UnifiedReviewView.vue` 的原状态列表；截图删除
  `knowledge-library-linux.png`；文档按白名单逐项撤销。

## 12. 交付前自检

- [x] G2 AC1-AC36 均有任务承接，无遗漏。
- [x] 接口清单与任务清单对应，字段统一使用 `status`、`ReviewStatus`、
  `draftId`、`knowledge-library-linux.png`。
- [x] 设计未写成代码转录本，仅确认签名、文件、合同和完成门槛。
- [x] 实现者冷读：已收集问题并完成以下裁决。

冷读裁决：

- G3 文档属于“本轮收敛文档”，补入 AC9 白名单。
- 有效 `draftId` 的详情状态优先于 URL `status`，并重新加载对应列表。
- 产品设计不再依赖未纳入本轮 Git 的旧架构文档路径。
- 保留现有侧栏无障碍名称，避免无需求依据地扩大 E2E 改动面。
- AC24 增加四个终态空列表测试，不依赖泛化说明。
