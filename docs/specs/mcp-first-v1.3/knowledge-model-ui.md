# Knowledge Model 结构化维护规格

> 需求名：mcp-first-v1.3-knowledge-model-ui ｜ 日期：2026-11-05

## 1. 目标

将当前单一 JSON 文本框升级为可实际操作的知识模型维护页面。首版结构化编辑：

- 知识类型
- 类型内属性
- 模板
- 关系类型

来源规则和工作流继续使用高级 JSON 编辑，避免在第一版 UI 中引入未验证的复杂表单。

## 2. 数据与接口

- 不新增数据库表、Migration 或 HTTP 接口。
- 继续使用 `GET/PUT /api/knowledge-model`、If-Match、CSRF 和 Idempotency-Key。
- 前端必须把读取合同中的 `key` 映射为写入合同的 `typeKey`、`propertyKey`、`templateKey`、`relationKey`、`workflowKey`。
- 新增定义的实体 ID 使用 `null`，服务端生成 UUID。
- 停用使用 `isEnabled=false`；不提供硬删除。

## 3. 页面

`/settings/knowledge-model` 提供四个区域：

1. 类型与属性：选择/新增类型，编辑类型字段和属性列表。
2. 模板：选择/新增模板，编辑类型归属、正文模板和字段 JSON。
3. 关系类型：选择/新增关系，编辑方向与启用状态。
4. 高级 JSON：来源规则和工作流数组。

保存前解析所有 JSON 字段；格式错误时不得发送请求。

## 4. 验收标准

- WHEN 用户打开知识模型页面 THEN 页面显示当前模型版本和四类维护区域。
- WHEN 用户新增类型或属性 THEN 请求中的实体 ID 为 `null`。
- WHEN 用户修改既有类型、属性、模板或关系 THEN 原实体 ID 保留。
- WHEN 保存模型 THEN 请求字段使用服务端写入合同名称，而不是 GET 合同中的短 `key`。
- WHEN 任一 JSON 字段无效 THEN 页面显示错误且不发送 PUT。
- WHEN 停用定义 THEN 只把 `isEnabled` 改为 false。
- WHEN 使用桌面和移动端访问 THEN 页面可操作且 Axe 无 serious/critical 问题。

## 5. 非目标

- 不新增 SourceRule/Workflow 的完整可视化表单。
- 不提供硬删除。
- 不新增模型版本历史页面。
- 不改变服务端模型校验规则。
