# 实施进度台账

> 需求名：mcp-first-v1.3-p0 ｜ 更新：2026-11-05

| 任务 | 状态 | 证据 / 裁决 |
|---|---|---|
| T0 新 Schema 与领域模型 | 完成 | 单一 `001_mcp_first_baseline.sql`；Catalog 1；Schema 1 检查和 89 项 Core 测试通过 |
| T1/T2 Knowledge Model GET/PUT | 完成 | `KnowledgeModelApiTests` 覆盖 GET/PUT 和模型版本 2 |
| T3/T4 MCP Resource/Tool | 完成 | `McpKnowledgeBaseTests` 覆盖 Resource、Tool 清单和 Draft-only |
| T5-T10 Source API | 完成 | Source 列表/详情/版本/内容/Locator 已接入；`KnowledgeBaseApiTests` 覆盖 Source Draft Apply 后读取 |
| T11-T18 Knowledge Search/Read/Graph | 完成 | Keyword/Fulltext、详情、Revision、Frontmatter、关系和局部图接口已接入；Fulltext 集成测试通过 |
| T19-T28 Unified Draft | 完成 | 创建/修改/Rebase/送审/放弃/应用/拒绝统一状态机；Source 和 Knowledge Apply 集成测试通过 |
| T29/T30 Dashboard/Audit | 完成 | Dashboard 已接入；Audit 改为通用 target 列查询 |
| T31-T45 MCP Tools | 完成 | 只读 Source/Knowledge、局部图和统一 Draft Tools；正式写入/审核工具未注册 |
| T46-T54 最小 Web UI | 完成 | 首页、知识库、收集箱、来源、模型维护、统一审核、图谱和设置导航已接入 |
| T55 旧源码下线 | 完成 | 旧 Document、Semantic/Embedding/Hybrid、Direct Write、明文导入导出源码与测试已删除；旧 Phase7/Phase8 Probe 已送回收站 |
| T56 全量收敛 | 完成 | 后端 327 测试（Core 93、Application 70、Server 164）、前端 36 测试、2 条 P0 E2E、build/format/lint 全部通过；Linux ARM64 按用户决定留到后续统一执行 |
| T57 MCP Prompts | 完成 | 通过 SDK 2.3.0 `WithPromptsFromAssembly` 注册 6 个工作流模板；不新增 Schema、REST API、Agent Runtime 或聊天历史 |
| T58 Prompts 验证 | 完成 | `McpPromptTests` 覆盖固定 6 项清单和六次 `prompts/get`；后端 328 测试（Core 93、Application 70、Server 165）通过 |
| T59 MCP 接入信息 | 完成 | 新增 `GET /api/mcp/info`，由 Server 单一目录返回 transport、Draft-only、17 个 Tools 和 6 个 Prompts；不包含 Token |
| T60 MCP Web 导航 | 完成 | 新增 `/settings/mcp`、配置模板复制、Token 面板与能力目录；后端 329 测试、前端 37 测试、3 条 E2E 和 Axe 检查通过 |
| T61 Knowledge Model UI | 完成 | 知识类型、属性、模板、关系类型结构化维护；新增定义使用 null ID，保存时映射 typeKey/propertyKey/templateKey/relationKey |
| T62 Knowledge Model UI 验证 | 完成 | 后端 329 测试（Core 93、Application 70、Server 166）、前端 38 测试、4 条 E2E、build/format/lint 全部通过 |
| T63 Source 定位读取 UI | 完成 | Sources 页面支持来源版本切换、章节/段落定位列表与定位区间文本读取；不新增后端接口 |
| T64 Source 定位验证 | 完成 | E2E 实际读取 Section 定位并确认正文；前端 38 测试、4 条 E2E、build/type/lint 通过 |
| T65 MCP 官方客户端 E2E | 完成 | 使用 `ModelContextProtocol.Client` 连接真实 HTTP `/mcp`，验证发现、Prompt、Draft-only 和 Resource |
| T66 来源拆解与综合验证 | 完成 | 外部客户端创建 Source/Knowledge Draft，Web 审核后保留 Locator；跨两个 Source 的综合 Knowledge 保留两个 SourceReference |
| T67 知识复盘工作流 | 完成 | 新增 UserUi `POST /api/knowledge/retrospective-draft`；1～20 条有序 Knowledge 生成 reflection Draft，来源去重前 8 条，MCP 拒绝，CSRF、幂等和审计复用统一 Draft |
| T68 知识复盘验证 | 完成 | 后端 343 测试（Core 93、Application 77、Server 173）、前端 43 测试、6 条 E2E、build/format/lint/typecheck 全部通过；Linux ARM64 仍留后续统一执行 |
| T69 知识综合工作流 | 完成 | 新增 UserUi `POST /api/knowledge/synthesis-draft`；用户选择 1～20 条 Knowledge 和启用类型，生成固定七章节综合脚手架 Draft，提供 Review 深链与可复制 MCP 任务；M2/M3 共用生成规则 |
| T70 知识综合验证 | 完成 | 后端 351 测试（Core 93、Application 81、Server 177）、前端 48 测试、8 条 E2E，Debug/Release build、format、lint、typecheck 全部通过；MCP 官方客户端与 M2 复盘回归通过；Linux ARM64 留后续统一执行 |
| T71 统一 Draft 审核编辑 | 完成 | Review 支持 Knowledge、Source、Relation 结构化编辑；UserUi 可编辑 pending_review 并回退 draft；字段级 changes/validation、完整 baseSnapshot、409 冲突和 dirty guard 已接入 |
| T72 Draft 审核编辑验证 | 完成 | 后端 359 测试（Core 93、Application 86、Server 180）、前端 51 测试、12 条 E2E，Debug/Release build、format、lint、typecheck 全部通过；M2/M3/MCP 回归通过；Linux ARM64 留后续统一执行 |
| T73 知识整理队列 | 完成 | 新增只读 `GET /api/knowledge/triage` 与 `/triage` 页面；动态计算缺来源、缺 required 属性、stale，支持 reason/type/staleDays/search、counts、Keyset cursor 和 AI 整理任务复制 |
| T74 知识整理验证 | 完成 | 前端 56 测试、14 条 E2E 通过；10,000 条 active Knowledge 的 Triage P95 门槛测试通过 |
| T75 v1.3 审查修复 | 完成 | 修复 Draft 审计、证据只读、模型约束、幂等过期、来源顺序、MCP revision 和 Windows 产物验证 |
| T76 全量验收 | 完成 | 后端 381 测试、前端 56 测试、14 条 E2E、format/build 及 Windows/ARM64 实际产物验收通过；ARM64 证据 SHA256 为 `79dfe98dcdcb010b1079004782e234a6ffae4d99f9c820923ffb0507b277d85b` |
| T77 v1.3 基线收敛 | 进行中 | 已统一成熟度、整理队列、审核历史和 MCP 工作流口径；AI 工作区补齐四种终态筛选；后端 381、前端 67、Windows/Linux E2E 各 14 通过；GitHub CI 待提交后验证 |
