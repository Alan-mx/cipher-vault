# MCP 阶段四端到端验证规格

> 需求名：mcp-first-v1.3-mcp-e2e ｜ 日期：2026-11-05

## 1. 验证基线

未指定第三方 AI 客户端前，先使用官方 `ModelContextProtocol.Client` 作为外部 MCP 客户端基线。它必须通过真实 HTTP `/mcp` 连接，不使用测试专用内部调用。

协议级验证通过后，真实第三方 AI 客户端的 Resources/Prompts 支持差异作为兼容性复核项。

## 2. 数据结构与接口

- 不新增表、Migration、REST 接口或 MCP Tool。
- 测试客户端通过 Bearer Token、VaultSession 和当前 `/mcp` 合同连接。
- Draft 审核仍调用现有 Web API，不允许外部客户端 apply/reject。

## 3. 端到端场景

### 3.1 发现

- 获取 Server Info、Instructions 和 Capabilities。
- 列出 17 个 Tools、6 个 Prompts 和 Knowledge Model Resource。
- 读取 `cipher-vault://knowledge-model`。

### 3.2 来源拆解

1. 外部客户端创建 Source Draft。
2. Web 用户提交并批准 Source。
3. 外部客户端读取 SourceRevision、Locator 和定位内容。
4. 外部客户端创建带 SourceReference 的 Knowledge Draft。
5. Web 用户提交并批准 Knowledge。
6. 验证正式 Knowledge 保留 sourceId、sourceRevision、locator、excerpt。

### 3.3 跨来源综合

1. 创建第二个 Source 和对应 Knowledge。
2. 外部客户端搜索并读取两份 Knowledge。
3. 外部客户端创建综合 Knowledge Draft，包含两个 SourceReference。
4. Web 用户批准后验证两个来源依据均存在。

## 4. 验收标准

- WHEN 官方客户端连接 THEN 初始化成功并返回当前 Server Instructions。
- WHEN 列出能力 THEN Tools、Prompts 和 Resource 与服务器目录一致。
- WHEN 外部客户端创建 Draft THEN 只能进入 Draft，不产生正式 Knowledge。
- WHEN Web 用户批准 Draft THEN 正式数据可被客户端读取。
- WHEN 来源拆解完成 THEN SourceRevision、Locator 和 Excerpt 完整保留。
- WHEN 跨来源综合完成 THEN 至少两个来源版本依据同时存在。
- WHEN 外部客户端尝试 apply/reject THEN 工具不存在。

## 5. 非目标

- 不自动调用任何第三方 Chat LLM。
- 不验证模型回答质量，只验证 MCP 合同和数据闭环。
- 不引入 Agent Runtime、聊天历史或外部客户端配置存储。
