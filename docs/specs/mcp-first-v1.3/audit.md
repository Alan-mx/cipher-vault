# CipherVault v1.3 MCP-first 能力审计

> 日期：2026-11-05
>
> 输入：`docs/CipherVault v1.3 产品与知识架构设计.md`、当前工作区 `main@HEAD` 实现
>
> 阶段：G1 现状审计；不修改数据库 Schema、MCP 工具契约或安全边界
>
> 说明：本文记录改造前现状，仅作决策依据；当前实施状态以 `progress.md` 为准。

## 1. 审计结论

当前实现已经具备 MCP-first 的部分基础，但还不是 v1.3 描述的通用个人知识基础设施：

- 已有真实 MCP HTTP、Bearer Token、VaultSession、Instructions、27 个 Tools、Draft/Review/Audit 和关系建议。
- MCP SDK 2.3.0 支持 Resources 和 Prompts，但当前服务只注册了 Tools 和 Instructions。
- 当前核心对象只有 `documents`，Source 与 Knowledge 没有独立模型；“来源”主要是 Draft 审核上下文中的可选文本，不是可查询、可定位、带版本的正式对象。
- 知识类型、属性、模板、关系类型均为代码内固定或 Frontmatter 约定，不是用户可配置的 Knowledge Model。
- 当前搜索实现包含真实可选的 Semantic/Hybrid，但 v1.3 当前阶段只要求 Keyword/Fulltext；Web 默认仍为 Hybrid，MCP 也暴露 Semantic/Hybrid。
- 没有 `cipher-vault://knowledge-model` Resource、`get_knowledge_model` Tool、标准 Prompt 模板或资源不可用时的等价 Tool。

结论：阶段一审计已完成。进入阶段二前，需要先确认 v1.3 的产品范围、数据模型和是否删除现有语义检索/直接写入路径。

## 2. MCP SDK 与协议能力

| 项目 | 当前实现 | 证据 | 结论 |
|---|---|---|---|
| SDK | `ModelContextProtocol.AspNetCore` / Core 2.3.0 | `v2/src/CipherVault.Server/packages.lock.json` | 可用 |
| 传输 | MCP HTTP `/mcp`，Stateless Session | `McpServerRegistration.cs` | 可用 |
| 鉴权 | Bearer Token + MCP policy | `McpBearerAuthentication.cs`、`McpServerRegistration.cs` | 可用 |
| Instructions | 静态 `ServerInstructions` | `McpServerRegistration.cs` | 已有基础说明，未覆盖 v1.3 全部规则 |
| Tools | `.WithToolsFromAssembly(...)` | `McpServerRegistration.cs` | 已注册 27 个 |
| Resources | SDK 2.3.0 支持 `WithResources*`、`McpServerResource*` | NuGet XML 文档 | 当前未注册 |
| Prompts | SDK 2.3.0 支持 `WithPrompts*`、`McpServerPrompt*` | NuGet XML 文档 | 当前未注册 |

## 3. MCP Tools 清单

当前共 27 个：

- Vault：`vault_status`
- 文档读取/发现：`list_notes`、`read_note`、`scan_notes`、`list_tags`、`get_document_map`、`get_backlinks`、`find_unlinked_mentions`
- 搜索/图谱：`search_notes`、`get_local_graph`
- 正式文档写入：`create_note`、`append_note`、`update_note`、`patch_note`、`delete_note`
- Draft：`list_drafts`、`read_draft`、`create_draft`、`update_draft`、`rebase_draft`、`submit_draft_for_review`、`discard_draft`
- 关系建议：`create_relation_suggestion`、`list_relation_suggestions`、`read_relation_suggestion`、`update_relation_suggestion`、`discard_relation_suggestion`

与 v1.3 能力分组的差距：

| v1.3 能力 | 当前覆盖 | 差距 |
|---|---|---|
| 获取知识模型 | 无 | 缺 Resource 与兼容 Tool |
| 关键词/全文搜索 | 部分 | 只有 `keyword` 模式；内部对长查询使用 FTS5、短查询扫描，没有独立 `fulltext` 合同 |
| 查询知识详情 | 部分 | `read_note` 读取 document，不是独立 Knowledge 模型 |
| 查询来源上下文 | 不足 | 无正式 Source 查询/读取章节/定位 API |
| Draft 创建与修改 | 已有 | MCP 不能审核，符合边界 |
| 关系建议 | 已有 | 类型固定为 5 个，不可配置；证据只指向 document revision |
| 辅助组织建议 | 无专用接口 | 可由外部 AI 生成 Draft，但缺类型/属性/标签建议合同 |

## 4. 搜索与 Embedding 现状

- 领域模型 `SearchMode` 只有 `Keyword`、`Semantic`、`Hybrid`。
- `SearchStore` 在关键词查询达到 3 个 Unicode scalar 时使用 FTS5，短查询使用有界扫描。
- `SearchService` 已接入 Semantic Provider、向量 Profile、远程同意和 Hybrid RRF。
- Web 默认搜索模式为 `hybrid`，MCP `search_notes` 接受 `semantic` 和 `hybrid`。
- v1.3 将 Embedding/向量检索定义为“当前不要求”，并要求不要暴露无法工作的语义模式，但没有明确现有已实现功能应删除还是仅从默认产品路径隐藏。

影响：若直接按 v1.3 改默认值但不裁决代码归属，会同时存在“已实现语义检索”和“当前阶段不做语义检索”两套口径。

## 5. 知识模型、Source 与 Knowledge

### 5.1 当前数据对象

`002_documents.sql` 只有统一的 `documents` / `document_revisions` / `document_links` / `tags` 等表。`type`、`project`、`maturity` 等语义来自 Frontmatter 动态键，没有正式的：

- KnowledgeType / PropertyDefinition / Template 主数据
- RelationType 主数据
- Source 对象
- SourceRevision 对象
- SourceReference 对象

### 5.2 当前知识视图

`KnowledgeViewField` 固定为 `Project`、`Type`、`Maturity`；成熟度固定为 Raw/Excerpt/Organized/Verified/Practiced。这与“默认配置可调整”不冲突于首版，但不能作为不可配置的领域枚举长期存在。

### 5.3 来源溯源

当前 Draft `reviewContext.sources` 只有：

- `documentId?`
- `title?`
- `virtualPath?`
- `reference?`
- `excerpt`

缺少 v1.3 必需的 `sourceRevision`、`locatorType`、`locatorValue`、`extractionMetadata`，也不存在跨来源综合的正式关联表。它目前是“提交方提供的未核实展示上下文”，不能承担正式来源证据职责。

## 6. Draft、Review 与 Audit

已有可复用能力：

- Create/Update/Trash Draft、revision、幂等、冲突、送审、拒绝、应用。
- AI 工作区列表、详情、字段级 Diff、来源提示、批量审核。
- MCP 不能 apply/reject。
- 审计使用白名单 details，不记录正文。

需要调整的点：

- MCP 正式写入工具仍始终列出，Direct Write Mode 开启后可直接修改正式文档；是否保留该模式需要 G1 裁决。
- Draft 来源上下文不是正式 SourceReference。
- 当前 Draft 是文档变更草稿，不是通用“知识对象创建/类型/属性/关系/来源引用”变更草稿。

## 7. 关系建议

- 已有完整 MCP 创建/读取/更新/撤回和 UserUi 审核链路。
- 有 revision stale、幂等、审计、正式关系软撤销。
- `RelationType` 固定为 `related`、`example_of`、`implements`、`contrasts_with`、`depends_on`。
- v1.3 默认建议 10 类语义关系并允许用户调整，因此关系类型需要从代码枚举升级为可配置模型，同时保留稳定的内置键和审核约束。

## 8. Web UI

当前主导航：知识库、知识视图、AI 工作区、知识网络、回收站、导入、活动记录、设置。

v1.3 建议导航：首页、知识库、收集箱、来源、知识网络、AI 工作区、设置。

差距：

- 无首页、收集箱、来源页面。
- 当前“知识视图”围绕 Project/Type/Maturity，仍带项目中心痕迹。
- 无知识模型/模板配置界面。
- 无来源读取和定位视图。

## 9. 安全边界

已具备：

- MCP Bearer Token 与 VaultSession 校验。
- 所有侧效 MCP 调用要求 UUID 幂等键。
- Cookie + CSRF 保护 Web 写接口。
- MCP 错误映射、响应限制和审计脱敏。
- 外部 AI 名称字段不作为身份凭据。

需要在 v1.3 实现时保持：

- Instructions 不替代服务端权限。
- Resource/Resource fallback Tool 不得绕过 VaultSession。
- 来源正文按不可信数据处理。
- 不默认返回整个 Vault。
- 不新增 Chat LLM、Agent Runtime、聊天历史或独立 Agent 授权。

## 10. Schema、备份与兼容

- 当前 `DatabaseMigrationCatalog.CurrentVersion = 9`，不是旧 `AGENTS.md` 所写的 5。
- 当前备份恢复最低 Schema 为 5，并存在 Schema 5/6/7/8 兼容测试。
- 项目尚未上线，用户已明确不需要兼容旧版本；新数据模型可以直接形成新基线。
- 在 v1.3 G3 数据设计确认前，不修改 Migration、备份范围或固定夹具。

## 11. 差距矩阵

| 优先级 | 差距 | 可复用点 | 建议去向 |
|---|---|---|---|
| P0 | MCP 只有 Instructions + Tools | 现有注册、鉴权、ToolInvoker | 阶段二增加 Knowledge Model Resource + 兼容 Tool |
| P0 | 无用户可配置 Knowledge Model | Frontmatter、知识视图、关系建议 | G3 先设计模型表/默认种子，再设计读写接口 |
| P0 | Source 与 Knowledge 混为 documents | 文档、revision、tag、FTS、Draft | G3 设计 Source/Knowledge/SourceReference，决定是否拆分表 |
| P0 | Draft 来源缺少版本/定位/提取元数据 | Draft reviewContext | 升级为正式 SourceReference，复用 Draft 审核 |
| P0 | AI 写入仍可切 Direct Write | 现有 Draft/Direct Mode | 裁决是否改为 MCP 永久 Draft-only |
| P1 | 搜索模式与 v1.3 不一致 | FTS、Short Scan、Semantic | 裁决删除/隐藏 Semantic，补 `fulltext` 合同 |
| P1 | 关系类型固定 5 类 | 关系建议/审核 | 改为配置模型 + 内置默认关系 |
| P1 | 无标准 MCP Prompts | Draft、搜索、关系、成熟度 | 阶段三实现 6 个工作流 Prompt |
| P1 | Web 导航与来源/收集箱不匹配 | 现有 WorkspaceLayout、Documents、Review | 阶段三按已确认范围改造 |

## 12. 审计边界

本审计未修改：

- SQL Migration
- 数据库 Schema
- MCP Tool contract
- 备份/恢复策略
- 前端页面

下一步必须先完成 G1 澄清，再进入 G2 规格和 G3 数据/接口设计。
