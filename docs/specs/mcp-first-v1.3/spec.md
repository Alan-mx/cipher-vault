# 规格（G2）

> 需求名：mcp-first-v1.3-p0 ｜ 日期：2026-11-05 ｜ 状态：已实施并完成验收
>
> 输入：`docs/CipherVault v1.3 产品与知识架构设计.md`、`clarify.md`、`audit.md`

## 背景与目标

CipherVault 当前已有 MCP、Draft、审计和检索基础，但 Source 与 Knowledge 混用文档模型，知识类型、属性、模板和关系不可配置，MCP 仍可切换为正式写入，现有 Semantic/Hybrid 也不符合 v1.3 当前阶段方向。

本需求建立未上线版本的新基线，只完成 P0 闭环：

- 可配置 Knowledge Model。
- 独立 Source、SourceRevision、Knowledge 和 SourceReference 模型。
- 不依赖 Embedding 的真实 Keyword/Fulltext 搜索。
- MCP Resource/Tool 能发现知识模型、读取来源、搜索知识和创建 Draft。
- MCP 永久 Draft-only，所有正式 Knowledge、Source 和关系变更经 Web 用户审核。
- 最小 Web UI 提供首页、收集箱、来源、模型维护和统一审核入口。

本项目尚未上线，不提供旧 Schema、旧备份、旧 MCP 契约或旧前端路由的兼容承诺。

## 术语

- **Knowledge Model**：当前 Vault 的知识类型、属性定义、模板、关系类型、来源规则和工作流约束集合，带版本和更新时间。
- **Knowledge**：用户或外部 AI 经审核后保存的可独立理解、引用、检索或复用的知识内容。
- **Source**：原始资料来源，支持统一 `SourceKind + metadata`。
- **SourceRevision**：Source 内容或来源快照的不可变版本。
- **SourceReference**：Knowledge 对具体 SourceRevision 和定位位置的证据引用。
- **Draft**：统一审核变更，包含目标对象类型、操作类型和 payload。
- **Inbox**：收集箱，保存尚未整理的 Knowledge Draft 和待处理内容。
- **Formal Data**：审核通过后的 Knowledge、Source、SourceRevision、SourceReference、关系及其索引数据。

## 用户故事

- 作为 Vault 用户，我要维护自己的知识类型、属性、模板和关系，以便外部 AI 按我的知识结构工作。
- 作为 Vault 用户，我要录入不同来源及其版本，以便知识内容能追溯到原始依据。
- 作为 Vault 用户，我要在 Web 中审核外部 AI 提交的草稿，以便正式知识库不会被 AI 绕过审核修改。
- 作为外部 AI 客户端，我要发现知识模型、搜索知识、读取来源并创建 Draft，以便在用户授权范围内整理知识。
- 作为 Vault 用户，我要在首页、收集箱和来源页面完成最小日常流程，以便不依赖内置 AI 聊天。

## 验收标准（EARS）

### P0-1 Knowledge Model 与新基线

- AC1: WHEN 新 Vault 使用新基线初始化 THE SYSTEM SHALL 创建 Knowledge Model、Knowledge、Source、SourceRevision、SourceReference、Draft、关系、审计和全文索引所需结构。
- AC2: WHEN 系统提供 Knowledge Model THE SYSTEM SHALL 返回模型版本、更新时间、知识类型、属性定义、模板、关系类型、来源规则和工作流约束。
- AC3: WHEN 新 Vault 初始化 THE SYSTEM SHALL 内置 Note、Article、Book、Podcast、Plan、Reflection 六个可修改、可停用、可新增的知识类型。
- AC4: WHEN 新 Vault 初始化 THE SYSTEM SHALL 内置支持、反驳、补充、举例、应用、依赖、属于、相似、来源于、实践验证十个可修改、可停用、可新增的关系类型。
- AC5: WHEN 用户维护知识类型、属性、模板或关系 THE SYSTEM SHALL 持久化变更并递增 Knowledge Model 版本。
- AC6: WHEN 用户停用已有知识类型或关系类型 THE SYSTEM SHALL 保留其稳定标识和历史知识引用，不删除已引用数据。
- AC7: WHEN 用户定义知识属性 THE SYSTEM SHALL 保存所属知识类型、属性键、值类型、必填性、默认值、可选项和展示顺序。
- AC8: WHEN 用户定义模板 THE SYSTEM SHALL 保存模板名称、适用知识类型、字段结构和默认内容，并拒绝引用不存在的类型或属性。
- AC9: WHEN 系统验证 Knowledge、Draft 或关系类型 THE SYSTEM SHALL 只接受当前模型允许的启用项，并允许历史正式数据继续读取已停用项。
- AC10: WHEN Knowledge Model 变更 THE SYSTEM SHALL 写入审计事件，且审计不记录正文、来源摘录或密钥。
- AC11: WHEN 已有 Knowledge 的模型定义发生变化 THE SYSTEM SHALL 不静默重写已有正文或属性；不兼容变更必须形成新的 Draft。
- AC12: WHEN MCP 客户端读取知识模型 THE SYSTEM SHALL 返回当前 Vault 的实际配置，而不是代码内固定方法或静态示例。

### P0-2 Source 独立模型

- AC13: WHEN 用户或外部 AI 创建 Source Draft THE SYSTEM SHALL 接受统一 `SourceKind + metadata`，首版支持 `web_page`、`local_file`、`book`、`podcast`、`other`。
- AC14: WHEN Source 缺少当前 SourceKind 或来源规则要求的元数据 THE SYSTEM SHALL 拒绝创建或送审，并返回可定位到具体字段的校验错误。
- AC15: WHEN Source 创建成功 THE SYSTEM SHALL 创建不可变 SourceRevision，并返回稳定 SourceId、SourceRevisionId 和版本号。
- AC16: WHEN 已发布 Source 的内容或来源快照发生变化 THE SYSTEM SHALL 创建新 SourceRevision，不覆盖旧版本内容。
- AC17: WHEN 外部 URL 来源保存内容快照 THE SYSTEM SHALL 将快照保存在独立 SourceRevision 中，使 URL 后续变化不影响既有引用。
- AC18: WHEN 系统读取 Source 内容 THE SYSTEM SHALL 支持有界全文读取和按章节或段落定位读取。
- AC19: WHEN 来源规则声明 `page` 或 `timestamp` 定位 THE SYSTEM SHALL 保存相关字段，但首版只要求章节和段落定位可读写。
- AC20: WHEN 用户归档 Source THE SYSTEM SHALL 软删除或归档该 Source，不物理删除历史 SourceRevision。
- AC21: WHEN Source 仍被正式 Knowledge 引用 THE SYSTEM SHALL 允许保留归档记录用于引用读取，但拒绝无引用处置依据的物理删除。
- AC22: WHEN 用户列出或筛选 Source THE SYSTEM SHALL 返回当前状态、SourceKind、标题、版本号和更新时间，不默认返回完整正文。
- AC23: WHEN Source 读取请求包含非法 offset、上限、定位类型或版本 THE SYSTEM SHALL 返回稳定错误且不返回其他来源内容。
- AC24: WHEN Source 数据被创建、归档或新增版本 THE SYSTEM SHALL 写入审计事件。

### P0-3 Knowledge 与 SourceReference

- AC25: WHEN 系统保存 Knowledge THE SYSTEM SHALL 以 Markdown 为正文字段，以数据库中的类型和结构化属性为权威数据。
- AC26: WHEN 导入或导出 Frontmatter THE SYSTEM SHALL 将其映射到结构化字段并校验模板；不一致时拒绝覆盖结构化主数据。
- AC27: WHEN Knowledge 引用知识类型和属性 THE SYSTEM SHALL 拒绝不存在的类型、未知属性和不符合属性值类型的数据。
- AC28: WHEN 已发布 Knowledge 内容或结构化属性变化 THE SYSTEM SHALL 创建不可变 Knowledge Revision，不覆盖历史版本。
- AC29: WHEN Knowledge 被创建或修改 THE SYSTEM SHALL 通过统一 Draft 审核流程完成，不接受 MCP 直接写正式 Knowledge。
- AC30: WHEN Knowledge 保存来源依据 THE SYSTEM SHALL 允许同时引用多个 SourceRevision。
- AC31: WHEN 创建 SourceReference THE SYSTEM SHALL 持久化 SourceId、SourceRevision、LocatorType、LocatorValue、Excerpt 和 ExtractionMetadata。
- AC32: WHEN SourceReference 指向不存在的 Source 或 SourceRevision THE SYSTEM SHALL 拒绝保存。
- AC33: WHEN SourceReference 的定位类型或定位值无法在当前 SourceRevision 中解析 THE SYSTEM SHALL 拒绝保存。
- AC34: WHEN SourceReference 被 Knowledge Revision 使用 THE SYSTEM SHALL 保持引用不可变；修改依据必须形成新的 Knowledge Draft。
- AC35: WHEN 跨来源综合 Knowledge 引用了多个 SourceRevision THE SYSTEM SHALL 在读取和审核界面保留全部有效来源依据。
- AC36: WHEN Knowledge 被归档 THE SYSTEM SHALL 保留其 Revision 和 SourceReference 以支持审计和历史读取，不静默破坏来源关联。
- AC37: WHEN Knowledge 包含标签、类型和属性 THE SYSTEM SHALL 在同一审核应用中原子保存并更新全文索引。
- AC38: WHEN 用户创建普通 Knowledge THE SYSTEM SHALL 不强制填入 Project、PARA 或其他固定方法字段。

### P0-4 Keyword / Fulltext 搜索

- AC39: WHEN 客户端选择 `keyword` 模式 THE SYSTEM SHALL 执行不含 Embedding 的确定性匹配，并支持标题、标签、知识类型和来源过滤。
- AC40: WHEN 客户端选择 `fulltext` 模式 THE SYSTEM SHALL 使用本地全文索引检索 Knowledge 正文，并支持与 `keyword` 相同的过滤条件。
- AC41: WHEN 客户端传入 `semantic` 或 `hybrid` THE SYSTEM SHALL 返回稳定 `invalid_search_mode` 或等价输入错误，不降级伪装成功。
- AC42: WHEN 搜索结果返回 THE SYSTEM SHALL 默认提供 KnowledgeId、标题、类型、摘要、标签、来源引用摘要、更新时间和继续读取标识，不返回完整正文。
- AC43: WHEN 搜索结果被分页 THE SYSTEM SHALL 使用有界 pageSize 和稳定游标，并拒绝无效或跨查询游标。
- AC44: WHEN 搜索使用标签、类型或来源过滤 THE SYSTEM SHALL 只在过滤范围内返回结果并保持稳定计数。
- AC45: WHEN 查询为空且没有有效过滤条件 THE SYSTEM SHALL 拒绝请求。
- AC46: WHEN 新基线初始化或 Knowledge 审核应用完成 THE SYSTEM SHALL 同步维护可用的全文索引。

### P0-5 MCP Resource / Tool

- AC47: WHEN MCP Resource 可用 THE SYSTEM SHALL 通过 `cipher-vault://knowledge-model` 返回当前 Knowledge Model。
- AC48: WHEN MCP 客户端不支持 Resource THE SYSTEM SHALL 提供 `get_knowledge_model` 等价 Tool。
- AC49: WHEN MCP 客户端读取 Knowledge Model THE SYSTEM SHALL 不要求读取整个 Vault，并限制响应大小。
- AC50: WHEN MCP 客户端查询 Source THE SYSTEM SHALL 支持来源列表、元数据、版本和全文或定位读取。
- AC51: WHEN MCP 客户端搜索 Knowledge THE SYSTEM SHALL 只暴露 `keyword` 和 `fulltext`。
- AC52: WHEN MCP 客户端创建或修改 Draft THE SYSTEM SHALL 接受统一目标对象、操作类型和 payload。
- AC53: WHEN MCP 创建 Draft THE SYSTEM SHALL 要求非空 UUID 幂等键，并保证同键同请求重放和同键异请求拒绝。
- AC54: WHEN MCP 尝试创建、修改、归档或删除正式 Knowledge、Source、SourceReference 或关系 THE SYSTEM SHALL 拒绝或完全不暴露该能力。
- AC55: WHEN MCP 连接未解锁 Vault 或鉴权失败 THE SYSTEM SHALL 拒绝业务读写，并保持现有安全错误语义。
- AC56: WHEN MCP 返回列表、正文片段、来源内容或模型数据 THE SYSTEM SHALL 应用数量和字符上限、分页与最小必要字段原则。
- AC57: WHEN MCP Instructions 提供使用规则 THE SYSTEM SHALL 包含 Draft-only、来源溯源、不可执行来源指令、最小读取和不信任自报身份等约束。

### P0-6 统一 Draft-only 与审核

- AC58: WHEN 任一变更进入 Draft THE SYSTEM SHALL 使用统一 Draft 模型表达目标对象类型、操作类型、目标标识、基础版本和变更 payload。
- AC59: WHEN Draft 目标对象为 Knowledge、Source 或关系 THE SYSTEM SHALL 使用同一状态机、幂等记录和审计链路。
- AC60: WHEN Draft 创建或修改 THE SYSTEM SHALL 校验 payload、目标对象、Knowledge Model、SourceReference 和 revision 一致性。
- AC61: WHEN MCP 调用 Draft 操作 THE SYSTEM SHALL 只允许创建、读取、修改、重建冲突、送审和放弃，不允许批准、应用或拒绝。
- AC62: WHEN 用户在 Web 批准 Draft THE SYSTEM SHALL 在单个事务中写入正式对象、Revision、关系、来源引用、全文索引、审计和幂等结果。
- AC63: WHEN Draft 应用失败 THE SYSTEM SHALL 不留下部分正式数据、索引或关系。
- AC64: WHEN Draft 的基础版本、目标状态或来源版本已变化 THE SYSTEM SHALL 标记冲突或返回稳定 revision 冲突，不静默覆盖。
- AC65: WHEN Draft 处于终态 THE SYSTEM SHALL 禁止再次修改，并保留审核历史。
- AC66: WHEN 用户拒绝或放弃 Draft THE SYSTEM SHALL 不改变正式 Knowledge、Source、SourceReference 或关系。
- AC67: WHEN 用户从收集箱快速记录内容 THE SYSTEM SHALL 创建 Knowledge Draft，不直接写入正式 Knowledge。
- AC68: WHEN Source 或关系变更需要审核 THE SYSTEM SHALL 使用统一 Draft 模型，不为来源导入或关系建议建立独立审核表。

### P0-7 最小 Web UI

- AC69: WHEN 用户进入工作区 THE SYSTEM SHALL 提供首页、知识库、收集箱、来源、知识网络、AI 工作区和设置入口。
- AC70: WHEN 用户打开首页 THE SYSTEM SHALL 展示最近 Knowledge、最近 Source、待审核 Draft、待整理内容和关系变化摘要，不展示 AI 聊天框。
- AC71: WHEN 用户打开收集箱 THE SYSTEM SHALL 支持快速记录为 Knowledge Draft、查看未整理 Draft 并进入审核流程。
- AC72: WHEN 用户打开来源页面 THE SYSTEM SHALL 支持来源列表、筛选、创建/修改 Draft、查看版本和定位读取内容。
- AC73: WHEN 用户打开模型维护页面 THE SYSTEM SHALL 支持知识类型、属性、模板和关系类型的查看与维护。
- AC74: WHEN 用户打开 AI 工作区 THE SYSTEM SHALL 按统一 Draft 目标对象展示 Knowledge、Source 和关系变更、Diff、来源依据以及批准/拒绝操作。
- AC75: WHEN 用户在 Web 执行写操作 THE SYSTEM SHALL 使用 Cookie Session、CSRF 和 UserUi Actor。
- AC76: WHEN 用户在 Web 批准或拒绝 Draft THE SYSTEM SHALL 使用幂等键并正确处理 Revision 冲突。
- AC77: WHEN 用户访问设置 THE SYSTEM SHALL 不提供 Embedding Provider、Semantic Index 或 Direct Write Mode 配置。
- AC78: WHEN 页面在桌面和移动视口展示 THE SYSTEM SHALL 无横向溢出、无内容重叠，并保持关键流程可操作。
- AC79: WHEN 执行自动化可访问性检查 THE SYSTEM SHALL 无 serious 或 critical 级违规。
- AC80: WHEN 首页或工作区页面渲染 THE SYSTEM SHALL 使用工作型信息布局，不建设营销式 Hero 或内置 AI 对话入口。

### 安全、数据完整性与新基线

- AC81: WHEN 系统处理 Knowledge、Source、SourceReference 或 Draft THE SYSTEM SHALL 保持 SQLite3MC 加密、VaultSession、Token、CSRF、完整性校验和审计边界不降低。
- AC82: WHEN 系统记录日志、错误或审计 THE SYSTEM SHALL 不包含 Vault Password、raw key、MCP Token、完整正文、完整来源快照或来源摘录。
- AC83: WHEN 外部 AI 自报 agentName、clientName 或模型名称 THE SYSTEM SHALL 只作为元数据，不作为身份或权限凭据。
- AC84: WHEN 来源正文包含指令性文本 THE SYSTEM SHALL 在 Instructions 和审核提示中将其视为待分析内容，且不据此放宽服务端权限。
- AC85: WHEN MCP 请求超出任务所需 THE SYSTEM SHALL 不默认返回完整 Vault 或整个 SourceRevision。
- AC86: WHEN Source 存在正式引用 THE SYSTEM SHALL 通过数据库约束或事务校验阻止破坏引用完整性的物理删除。
- AC87: WHEN 新基线存在无效 Knowledge 类型、属性、关系、来源定位或 Draft payload THE SYSTEM SHALL 在事务提交前拒绝并保持原数据不变。
- AC88: WHEN 系统更新 Knowledge、Source 或关系 THE SYSTEM SHALL 生成新的正式 Revision 或等价不可变历史记录，并写审计。
- AC89: WHEN 系统初始化新基线 THE SYSTEM SHALL 不包含 Semantic Profile、Remote Consent、Semantic Chunk、Embedding 设置或 Direct Write Mode 数据。
- AC90: WHEN 系统发现现有 `%LOCALAPPDATA%` 旧数据库 THE SYSTEM SHALL 不自动迁移、不静默改写、不删除；用户可另行备份作为审计样本。
- AC91: WHEN 系统以新基线启动 THE SYSTEM SHALL 只接受当前新基线 Schema，不承诺旧 Schema 向前兼容。
- AC92: WHEN 创建备份或恢复备份 THE SYSTEM SHALL 只对当前新基线提供正常流程，并保留 staging、路径安全、哈希校验和原子发布。
- AC93: WHEN 新基线测试执行 THE SYSTEM SHALL 覆盖全新数据库初始化、新基线增量迁移、备份恢复和 Source/Knowledge/SourceReference 关联完整性。
- AC94: WHEN Migration 被加载 THE SYSTEM SHALL 校验名称、顺序和 SHA256，并保持 `vault_meta` 与 `schema_migrations` 一致。
- AC95: WHEN 打开或迁移数据库失败 THE SYSTEM SHALL 不继续写入或发布半成品。

## 字段口径

| 对象 | 字段 | 类型 | 字典/枚举 | 必填 | 默认 | 说明 |
|---|---|---|---|---|---|---|
| Knowledge Model | version | integer | 单调递增 | 是 | 1 | 每次模型变更递增 |
| Knowledge Model | updatedAt | ISO 8601 UTC | - | 是 | 当前时间 | 当前模型更新时间 |
| Knowledge Type | typeId | string/UUID | - | 是 | 生成 | 稳定标识 |
| Knowledge Type | name | string | - | 是 | - | 用户显示名称 |
| Knowledge Type | description | string nullable | - | 否 | null | 用途说明 |
| Knowledge Type | isEnabled | boolean | - | 是 | true | 停用后保留历史引用 |
| Property Definition | propertyId | string/UUID | - | 是 | 生成 | 稳定标识 |
| Property Definition | key | string | 规范化键 | 是 | - | 类型内唯一 |
| Property Definition | valueType | enum | `string`、`number`、`boolean`、`date`、`enum`、`array`、`object` | 是 | - | 结构化属性值类型 |
| Property Definition | required | boolean | - | 是 | false | 模板或类型校验使用 |
| Property Definition | defaultValue | JSON nullable | - | 否 | null | 必须符合 valueType |
| Template | templateId | string/UUID | - | 是 | 生成 | 稳定标识 |
| Template | knowledgeTypeId | string/UUID | - | 是 | - | 必须引用启用或历史存在类型 |
| Template | fields | JSON | - | 是 | - | 字段结构与属性映射 |
| Relation Type | relationTypeId | string/UUID | - | 是 | 生成 | 稳定标识 |
| Relation Type | key | string | 规范化键 | 是 | - | 内置种子键稳定 |
| Relation Type | directed | boolean | - | 是 | 按种子 | 决定关系方向 |
| Relation Type | isEnabled | boolean | - | 是 | true | 停用后保留历史关系 |
| Source Rule | sourceRuleId | string/UUID | - | 是 | 生成 | 稳定标识 |
| Source Rule | sourceKind | enum | 与 SourceKind 一致 | 是 | - | 规则适用来源类型 |
| Source Rule | metadataSchema | JSON object | - | 是 | `{}` | 必填元数据和字段校验 |
| Source Rule | supportedLocatorTypes | enum array | `section`、`paragraph`、`page`、`timestamp` | 是 | 按类型 | 当前可用定位方式 |
| Source Rule | isEnabled | boolean | - | 是 | true | 控制新建与送审校验 |
| Workflow Definition | workflowId | string/UUID | - | 是 | 生成 | 稳定标识 |
| Workflow Definition | key | string | 规范化键 | 是 | - | 工作流稳定键 |
| Workflow Definition | targetObjectTypes | enum array | `knowledge`、`source`、`relation` | 是 | - | 可作用对象 |
| Workflow Definition | constraints | JSON object | 有界 | 是 | `{}` | 模型版本、Draft 操作和来源要求等约束 |
| Workflow Definition | isEnabled | boolean | - | 是 | true | P0 先供模型发现和校验，不执行 Prompt |
| Source | sourceId | string/UUID | - | 是 | 生成 | 稳定标识 |
| Source | kind | enum | `web_page`、`local_file`、`book`、`podcast`、`other` | 是 | - | Source 类型 |
| Source | metadata | JSON | 按 SourceRule 校验 | 是 | `{}` | 标题、作者、URL、发布时间等 |
| Source | status | enum | `active`、`archived` | 是 | `active` | 归档不破坏引用 |
| Source Revision | sourceRevisionId | string/UUID | - | 是 | 生成 | 稳定标识 |
| Source Revision | sourceId | string/UUID | - | 是 | - | 所属 Source |
| Source Revision | revision | integer | 从 1 递增 | 是 | 1 | 来源版本号 |
| Source Revision | content | Markdown/Text | - | 是 | - | 当前版本内容或快照 |
| Knowledge | knowledgeId | string/UUID | - | 是 | 生成 | 稳定标识 |
| Knowledge | title | string | - | 是 | - | 标题 |
| Knowledge | summary | string | - | 是 | `""` | 搜索结果摘要，可由用户或审核通过的 Draft 提供 |
| Knowledge | content | Markdown | - | 是 | - | 正文主存储 |
| Knowledge | knowledgeTypeId | string/UUID | 当前模型 | 是 | - | 知识类型 |
| Knowledge | properties | JSON object | 按属性定义 | 是 | `{}` | 结构化属性权威数据 |
| Knowledge | tags | string array | 规范化标签 | 否 | `[]` | 检索和分类 |
| Knowledge | revision | integer | 从 1 递增 | 是 | 1 | 正式版本 |
| SourceReference | sourceReferenceId | string/UUID | - | 是 | 生成 | 稳定标识 |
| SourceReference | sourceId | string/UUID | - | 是 | - | 来源 |
| SourceReference | sourceRevision | integer | 存在的版本 | 是 | - | 精确来源版本 |
| SourceReference | locatorType | enum | 首版 `section`、`paragraph`；预留 `page`、`timestamp` | 是 | - | 定位类型 |
| SourceReference | locatorValue | JSON | 按定位类型 | 是 | - | 位置或路径 |
| SourceReference | excerpt | string | 有界文本 | 是 | - | 原文摘录 |
| SourceReference | extractionMetadata | JSON object | 有界 | 否 | `{}` | 提取方式、时间等 |
| Draft | targetObjectType | enum | `knowledge`、`source`、`relation` | 是 | - | 统一目标对象 |
| Draft | operationType | enum | `create`、`update`、`archive`、`restore`、`retract` | 是 | - | 统一操作 |
| Draft | targetId | string/UUID nullable | - | 条件必填 | null | create 可为空 |
| Draft | baseRevision | integer nullable | - | 条件必填 | null | update/archive 需要 |
| Draft | payload | JSON object | 按目标对象 | 是 | - | 有界变更数据 |
| Draft | status | enum | `draft`、`pending_review`、`conflict`、`applied`、`rejected`、`discarded`、`expired` | 是 | `draft` | 审核状态 |
| Search Result | mode | enum | `keyword`、`fulltext` | 是 | - | 不允许其他值 |
| Search Result | nextCursor | string nullable | - | 否 | null | 稳定分页游标 |

## 状态流转

### Draft

| 当前状态 | 目标状态 | 触发者/动作 | 约束 |
|---|---|---|---|
| 无 | `draft` | MCP/Web 创建 | 校验目标对象、模型、payload、幂等键 |
| `draft` | `draft` | MCP/Web 修改 | 必须递增 Draft Revision |
| `draft` | `pending_review` | MCP/Web 送审 | payload 和引用重新校验 |
| `draft` | `conflict` | 系统/编辑或送审 | 目标基础版本或来源版本变化 |
| `conflict` | `draft` | MCP/Web 重建 | 基于用户确认的新版本重建 |
| `pending_review` | `applied` | Web 用户批准 | 单事务写入正式数据；MCP 禁止 |
| `pending_review` | `rejected` | Web 用户拒绝 | 正式数据不变；MCP 禁止 |
| `draft`/`pending_review`/`conflict` | `discarded` | MCP/Web 放弃 | 终态不可再改 |
| 任一非终态 | `expired` | 系统按策略 | 仅后续策略启用；正式数据不变 |

### Source

| 当前状态 | 目标状态 | 触发者/动作 | 约束 |
|---|---|---|---|
| 无 | `active` | 用户批准 Source Draft | 创建 Source 和首个 SourceRevision |
| `active` | `active` | 用户批准更新 Draft | 追加 SourceRevision，不覆盖旧版本 |
| `active` | `archived` | 用户批准归档 Draft | 保留版本和引用可读性 |
| `archived` | `active` | 用户批准恢复 Draft | 允许恢复，稳定 SourceId 不变 |

### Knowledge

| 当前状态 | 目标状态 | 触发者/动作 | 约束 |
|---|---|---|---|
| 无 | `active` | 用户批准 Knowledge Draft | 创建 Knowledge Revision 和全文索引 |
| `active` | `active` | 用户批准更新 Draft | 追加 Revision 和 SourceReference 快照 |
| `active` | `archived` | 用户批准归档 Draft | 保留 Revision、引用和审计 |
| `archived` | `active` | 用户批准恢复 Draft | 稳定 KnowledgeId 不变 |

### Relation

| 当前状态 | 目标状态 | 触发者/动作 | 约束 |
|---|---|---|---|
| 无 | `active` | 用户批准 Relation Draft | 校验关系类型和两端对象 revision |
| `active` | `retracted` | 用户批准撤回 Draft | 保留关系历史与审计 |
| `retracted` | `active` | 用户批准恢复 Draft | 生成新的正式变更记录 |

## 权限矩阵

| 操作 | 角色 | 范围 |
|---|---|---|
| 读取 Knowledge Model、Knowledge、Source、关系、搜索结果 | MCP/Web 用户 | 已解锁 Vault；MCP 需 Bearer Token |
| 创建/修改/送审/放弃 Draft | MCP/Web 用户 | 已解锁 Vault；MCP 受 Token 和幂等约束 |
| 批准/拒绝 Draft | Web 用户 | Cookie Session + CSRF；MCP 无此能力 |
| 维护 Knowledge Model | Web 用户 | Cookie Session + CSRF |
| 建立/撤回关系 Draft | MCP/Web 用户 | 仅能创建 Draft；正式关系由 Web 用户批准 |
| 归档/恢复 Source 或 Knowledge | Web 用户批准 | 必须通过 Draft；保留历史 |
| 查看审计 | Web 用户 | 已存在的受保护审计入口 |
| 管理 MCP Token | Web 用户 | 现有 Token 管理边界 |
| 清理终端 Draft | System | 已解锁 Vault 的维护通道；不刷新活动；不修改正式对象 |

## 非目标（明确不做的）

- 不做内置 Ask AI、Chat LLM Provider、Agent Runtime、聊天历史或 Agent 身份授权。
- 不做 Embedding Provider、Semantic/Hybrid 搜索、向量表、Semantic Index 或对应 Web 设置。
- 不做 Direct Write Mode 或任何 MCP 正式写入工具。
- 不做旧 Schema、旧备份、旧 Draft payload、旧 MCP Tool 或旧前端路由兼容。
- 不自动迁移当前 `%LOCALAPPDATA%` 数据库。
- 本 P0 不实现 MCP Prompts、复杂标准工作流模板或 Agent 自动编排。
- 不实现页码、播客时间戳的完整定位生产支持；仅保留字段和模型能力。
- 不实现外部 URL 自动抓取、OCR、音视频转写或复杂来源解析。
- 不实现多人协作、多 Agent 授权或独立权限体系。
- 不强制使用 Project、PARA、Zettelkasten 或其他知识管理方法。
- 不建设营销式首页、Hero 页或 AI 对话首页。

## 影响面

- 涉及模块：
  - `CipherVault.Core`：Knowledge Model、Source、Knowledge、SourceReference、Search、Draft、关系、Migration。
  - `CipherVault.Application`：模型维护、来源读取、知识搜索、统一 Draft 审核事务。
  - `CipherVault.Server`：REST API、MCP Resource/Tool、Instructions、错误映射、审计。
  - `v2/frontend`：首页、收集箱、来源、模型维护、AI 工作区、设置和路由。
  - `v2/tests`：新基线 Schema、Backup、MCP、API、关联完整性和前端 E2E。
- 涉及旧表：
  - 新基线替换 `documents`/`document_revisions` 混合模型；具体表变更在 G3 数据设计确认。
  - 删除 Schema 8 Semantic 表和 Direct Write Mode 持久化数据。
  - 统一 Draft 模型替换专用关系建议审核表；具体迁移方式在 G3 确认。
- 关联端：
  - 当前只有本仓库 Web 与 MCP Server，无独立客户端仓库。
  - MCP 客户端兼容性以 SDK 2.3.0 实际支持和 P0 验收为准。

## G2 自检

- P0-1 至 P0-7 均有稳定 AC 覆盖。
- P0 范围之外的内容均进入非目标。
- G1 中已确认的 Semantic 移除、MCP Draft-only、Source/Knowledge 拆表、统一 Draft、无旧兼容均已写入 AC。
- 未在 G2 指定数据库表名、字段 DDL、HTTP 路径或 Tool 签名；这些留待 G3 分两步确认。
