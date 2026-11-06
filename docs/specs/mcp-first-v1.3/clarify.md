# 需求疑问清单（G1）

> 需求名：mcp-first-v1.3 ｜ 日期：2026-11-05
>
> 输入：`docs/CipherVault v1.3 产品与知识架构设计.md` + `audit.md`

## 一、歧义项

| # | 问题描述 | 涉及位置 | 建议默认答案 |
|---|---|---|---|
| 1 | 本轮要直接完成 v1.3 四阶段，还是先交付 MCP 能力审计后的 P0 闭环？ | v1.3 第十章 | 先交付 P0 闭环：Knowledge Model Resource/Tool、正式 SourceReference、MCP Draft-only、真实 Keyword/Fulltext；Prompts 与 Web 导航作为后续切片 |
| 2 | 现有 Semantic/Hybrid 已有完整实现，但 v1.3 当前不要求 Embedding。保留、隐藏还是删除？ | `SearchService`、`Semantic*`、Schema 8、Web 设置页 | 从 v1.3 产品路径移除 Semantic/Hybrid；因未上线也不保留旧兼容，后续新基线不包含 Schema 8 的语义表与 Embedding Provider |
| 3 | “AI 写入默认 Draft”是否允许用户临时开启 Direct Write Mode？ | `McpWriteModeService`、`McpWriteModePanel.vue` | MCP 改为永久 Draft-only，删除 Direct Write Mode、正式写入 MCP Tools 和相关测试 |
| 4 | Source 与 Knowledge 是否首版就拆成独立数据对象？ | `documents` 表、Draft、Search、UI | 是。Source、Knowledge、SourceReference 分别建模；不继续用同一 `documents` 表混合承担 |
| 5 | Knowledge Model 首版允许配置哪些内容？ | v1.3 4.2、5.1 | 类型、属性定义、模板、关系类型、来源规则和工作流约束全部建模；首版 UI 可只提供类型/属性/模板/关系维护 |
| 6 | 默认知识类型和关系类型要不要内置种子？ | v1.3 5.4、6.1 | 内置但不锁定。类型种子可为 Note、Article、Book、Podcast、Plan、Reflection；关系种子采用 v1.3 的 10 类，用户可停用或新增 |
| 7 | Source 读取粒度首版做到哪里？ | v1.3 4.5、5.5 | 首版支持来源列表、全文读取、按章节/段落定位读取和版本号；页码/时间戳定位保留字段但按来源类型逐步启用 |
| 8 | Web UI 是否本轮同步改成 v1.3 的“首页/收集箱/来源”导航？ | v1.3 第八章、当前 `WorkspaceLayout.vue` | 数据与 MCP P0 完成后同步做导航和最小页面；不做 AI 聊天页或营销式首页 |

## 二、缺失项

- **知识对象字段**：Knowledge 是否仍以 Markdown 正文为主，还是正文、摘要、属性全部结构化存储。建议正文仍为 Markdown，类型/属性必须有结构化主数据并从 Frontmatter 同步校验。
- **Source 形态**：Source 是否支持外部 URL、本地文件、书籍、播客等统一模型。建议统一 SourceKind + metadata，不把外部 URL 当成本地文档正文。
- **草稿类型**：现有 Draft 只支持 create/update/trash。是否增加知识创建、来源导入、关系建议、综合草稿等类型，还是统一为“知识对象变更 payload”。建议统一知识对象 Draft，避免每种工作流各建一套表。
- **审核权限**：MCP 只能创建/修改/送审，用户才能在 Web 审核。该边界建议保持不变。
- **删除口径**：Source 删除时已被 Knowledge 引用的内容如何处理。建议软删除且禁止删除仍被正式 Knowledge 引用的 Source，需先解除或保留归档。
- **兼容目标**：已确认不做旧版本兼容；仍需确认是否保留当前真实 `%LOCALAPPDATA%` 测试数据以外的一切旧 Migration 测试。

## 三、与现有逻辑冲突项

| # | 冲突描述 | 现有实现位置 | 建议 |
|---|---|---|---|
| 1 | v1.3 不要求 Embedding，当前 Web 默认 Hybrid 且 MCP 暴露 Semantic/Hybrid | `SearchService.cs`、`SearchContracts.cs`、`KnowledgeTools.cs`、`stores/search.ts` | 按未上线新基线移除或彻底隐藏语义能力，并补独立 `fulltext` |
| 2 | v1.3 要可配置知识模型，当前类型/关系/成熟度是固定枚举 | `RelationContracts.cs`、`KnowledgeViewContracts.cs`、`KnowledgeMaturityContracts.cs` | 关系与类型改为配置主数据；成熟度保留默认种子并允许停用/改名 |
| 3 | v1.3 Source 与 Knowledge 分离，当前均为 `documents` | `002_documents.sql`、`DocumentContracts.cs` | 新 Schema 直接拆 Source/Knowledge；避免 `recordKind` 混合表 |
| 4 | v1.3 要求完整来源定位，当前 Draft 只有 documentId/reference/excerpt | `DocumentDraftReviewContextCodec.cs` | 用正式 SourceReference 替换无版本展示上下文 |
| 5 | v1.3 次级导航含首页/收集箱/来源，当前没有这些页面 | `router/index.ts`、`WorkspaceLayout.vue` | 在 G2 明确页面范围后统一改造 |
| 6 | 当前 MCP 有 27 个工具，其中 5 个可正式写入 | `McpToolNames.cs`、`DocumentWriteTools.cs` | 若采纳 Draft-only，移除正式写入 MCP Tools，仅保留读取、搜索、Draft 与关系建议 |

## 四、用户回答区

用户于 2026-11-05 确认全部建议默认答案，并补充以下最终决策：

1. 本轮只完成 P0 闭环，不一次性完成 v1.3 四阶段。
2. Semantic/Hybrid、Embedding、向量表和 Direct Write Mode 从新基线彻底移除，不做隐藏或旧版本兼容。
3. MCP 永久 Draft-only。MCP 只能读取、搜索、创建/修改/送审 Draft；正式 Knowledge、Source 和关系只能由 Web 用户审核后的流程变更。
4. Source、Knowledge、SourceReference 首版直接拆分为独立对象和数据表，不再混用 `documents`。
5. Knowledge Model 完整建模类型、属性定义、模板、关系类型、来源规则和工作流约束；首版 UI 优先提供类型、属性、模板和关系维护。
6. 内置 Note、Article、Book、Podcast、Plan、Reflection 类型种子和 v1.3 的 10 类关系种子，允许修改、停用和新增。
7. Source 首版支持列表、全文读取、章节/段落定位读取和版本号；页码与时间戳定位字段预留。
8. P0 数据与 MCP 能力完成后改造最小 Web 导航：首页、收集箱、来源，不做 AI 聊天页和营销式首页。
9. Knowledge 正文以 Markdown 为主；类型和属性必须有结构化存储，数据库结构化字段是权威数据源；Frontmatter 只作为导入、导出和展示格式。
10. Source 使用统一 `SourceKind + metadata` 模型；外部 URL 不直接作为本地正文，需要快照时形成独立的不可变来源版本。
11. Draft 使用统一目标对象、操作类型和 payload，不为知识、来源、关系等工作流分别建 Draft 表。
12. Source 使用软删除/归档；仍被正式 Knowledge 引用的 Source 不允许直接删除，必须先解除引用或保留归档记录。
13. 不保留旧 Schema 向前兼容和历史兼容测试；保留新基线初始化、增量迁移、备份恢复和关联完整性测试。
14. 当前 `%LOCALAPPDATA%` 测试数据不自动迁移，可作为审计样本另行备份；新基线使用干净数据库初始化。
15. P0 范围按 P0-1 至 P0-7 执行；Prompts、复杂知识工作流和更多自动化能力放到后续切片。
