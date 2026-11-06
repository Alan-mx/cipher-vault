# 需求疑问清单（G1）

> 需求名：v1.3-baseline-convergence ｜ 日期：2026-11-06
>
> 输入：用户本次提供的 v1.3 实现差异审计结论；仓库基线
> `4afeecce24ffda4c804a5926bf28d3ba00650ece`；当前源码、README、
> `docs/CipherVault v1.3 产品与知识架构设计.md` 与 `docs/specs/mcp-first-v1.3/`。

## 一、已核实的现状

以下结论来自本次源码检查，不再作为未知项讨论：

- 当前 `HEAD` 与审计基线一致，提交为 `docs: 更新v1.3验收基线`。
- 整理队列只自动识别 `missing_required_property`、`missing_source`、`stale`
  三类原因；Draft/Conflict 仅显示数量并跳转 AI 工作区。
- 整理页当前只有查看详情和复制 AI 整理任务，没有直接整理、暂时保留、忽略、
  删除或归档操作。
- 代码没有固定的 Raw/Excerpt/Organized/Verified/Practiced 成熟度枚举；
  `KnowledgeStatus` 只有 `Active`、`Archived`。
- Draft 领域状态已经包含 `draft`、`pending_review`、`conflict`、`applied`、
  `rejected`、`discarded`、`expired`；列表 API 也接受全部状态。
- AI 工作区前端只允许 `pending_review`、`draft`、`conflict`、`applied`
  四个筛选值，并把 `applied` 显示为“历史”。
- Git `HEAD` 中没有 `docs/` 文件；本地 `docs/` 整个目录未跟踪。`AGENTS.md`
  和 README 又引用了该目录中的设计及验收资料。
- CI 的前端 E2E 在 `ubuntu-latest` 上运行；仓库目前只有
  `knowledge-library-win32.png`，没有 Linux 截图基准。
- 当前 MCP 没有知识复盘、跨来源综合的专用 Tool；这两个 Web 用例存在，
  MCP 侧通过 Prompt、读取/搜索、通用 Draft 和提交审核完成闭环。

## 二、歧义项

| # | 问题描述 | 涉及位置 | 建议默认答案 |
|---|---|---|---|
| 1 | 本轮“基线收敛”只统一文档、补齐明确缺口并修复 CI，还是要按旧设计继续补全整理队列等新能力？ | 审计结论“建议”；`docs/CipherVault v1.3 产品与知识架构设计.md`；当前 P0 基线 | 只做基线收敛：统一产品/设计/验收口径，补齐审核历史终态入口并修复 Linux E2E；重复检测、观点验证、整理操作和成熟度模型不并入本轮。 |
| 2 | 知识成熟度是否作为系统固定能力？ | 设计 6.1；`KnowledgeBaseContracts.cs` 的 `KnowledgeStatus`；Knowledge Model | 不设固定成熟度生命周期；如需成熟度，由 Knowledge Model 的自定义属性表达，整理规则不依赖固定状态流转。 |
| 3 | 整理队列的 P0 规则范围采用当前三类，还是补齐设计中的六类？ | 设计 6.2；`KnowledgeTriageStore.cs`；`KnowledgeTriageContracts.cs` | 当前基线保留三类规则；待审核 Draft 作为独立待办统计入口；“可能重复”和“需要验证”归入后续 P1，需另行走 G1/G2。 |
| 4 | 整理队列是否要增加直接整理、暂时保留、忽略、删除等逐项操作？ | 设计 6.2；`TriageView.vue`；Knowledge/Source 软删除约束 | 本轮保持只读队列和复制 AI 整理任务；未来涉及逐项操作时，“删除”只能定义为归档/软删除，并另独立澄清状态与权限。 |

## 三、缺失项

| # | 缺失口径 | 涉及位置 | 建议默认答案 |
|---|---|---|---|
| 5 | “历史审核结果”是一个聚合入口，还是按终态分别筛选？是否必须关联 Audit？ | 设计 8.2；`UnifiedReviewView.vue`；`UnifiedDraftEndpoints.cs` | Web 分别提供 Applied、Rejected、Discarded、Expired 四个终态筛选项；本轮复用现有列表 API，不新增跨状态聚合参数，也不承诺在 Draft 页面内联 Audit 明细。 |
| 6 | 哪些文档需要进入 Git，哪些继续作为本地工作产物？ | `AGENTS.md`；README；本地未跟踪的 `docs/` | 只纳入被 `AGENTS.md` 指定为产品/技术准绳的设计文档、`docs/specs/mcp-first-v1.3/` 基线与验收证据，以及本轮 convergence 的 spec/verification；其他 `docs/` 工作产物仍不纳入。 |
| 7 | Linux Playwright 截图缺失应以新增正式基准还是调整测试策略解决？ | `p0.spec.ts`；`p0.spec.ts-snapshots/`；`.github/workflows/ci.yml` | 在 Ubuntu CI 环境生成并固定 `knowledge-library-linux.png`，保留现有截图断言；重新执行完整 CI，Windows 本地结果不替代 Linux 证据。 |
| 8 | 知识复盘和跨来源综合是否需要新增专用 MCP Tool？ | MCP Prompt；通用 Draft Tool；复盘/综合 Web 服务 | 不新增专用 Tool，继续使用 Prompt + 读取/搜索 + 通用 Draft；该路径作为正式非目标写入规格，如要求专用 Tool 则升档为独立需求。 |

## 四、与现有逻辑冲突项

| # | 冲突描述 | 现有实现位置 | 建议 |
|---|---|---|---|
| 1 | 设计列出六类整理候选和多种操作，实现只有三类规则且页面只读。 | 设计 6.2；`KnowledgeTriageStore.cs`；`TriageView.vue` | 由问题 1、3、4 确定是改文档口径还是扩实现。 |
| 2 | 设计“建议默认提供”五阶段成熟度，代码没有固定成熟度领域模型。 | 设计 6.1；`KnowledgeBaseContracts.cs` | 由问题 2 确认后统一文档，不先新增固定枚举或 Migration。 |
| 3 | 设计要求查看历史审核结果，前端“历史”只查询 `applied`，后端已支持全部终态。 | 设计 8.2；`UnifiedReviewView.vue`；`UnifiedDraftEndpoints.cs` | 由问题 5 确认入口形态；优先只改前端，不改后端契约。 |
| 4 | `AGENTS.md` 把 `docs/` 作为方向依据，但 Git 基线中没有该目录。 | `AGENTS.md`；README；Git `HEAD` | 由问题 6 确认 Git 范围；不默认把整个本地 `docs/` 目录加入 Git。 |
| 5 | E2E 断言依赖平台截图，CI 运行 Linux 但仓库只有 Windows 基准。 | `p0.spec.ts`；`p0.spec.ts-snapshots/` | 由问题 7 确认；快照必须在 Linux 环境生成后重新验证。 |

## 五、用户回答区

2026-11-06 用户确认：八项全部采用建议默认答案，并明确以下边界：

1. 本轮只做 v1.3 基线收敛，不扩展新功能。
2. 不增加固定成熟度生命周期。
3. 整理队列保留现有三类规则。
4. 整理队列继续只读，不增加逐项操作。
5. AI 工作区补齐 Applied、Rejected、Discarded、Expired 四种终态历史筛选。
6. 只将指定的产品设计、P0 规格、验收证据和本轮收敛文档纳入 Git，不提交整个
   `docs/`。
7. Linux 截图基准正式入库，保留截图断言。
8. 不增加复盘和综合专用 MCP Tool。

状态：G1 已确认，进入 G2。
