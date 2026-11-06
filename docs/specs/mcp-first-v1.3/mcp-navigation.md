# MCP Web 导航 P1 规格

> 需求名：mcp-first-v1.3-mcp-navigation ｜ 日期：2026-11-05 ｜ 前置：MCP Prompts 已完成

## 1. 用户目标

用户需要在 Web 内确认外部 AI 应如何连接 CipherVault，以及当前 MCP 暴露了哪些能力。页面不是聊天界面，也不执行 MCP 请求。

## 2. 数据结构决定

- 不新增数据库表、Migration 或 Vault Meta 字段。
- 增加一个只读能力目录，由 Server 常量统一提供 Tools、Prompts、传输方式和 Draft-only 边界。
- Token 明文不进入能力目录；仍只在轮换后的一次性弹窗中显示。

## 3. 接口

`GET /api/mcp/info`

- Cookie Session + 已解锁 Vault。
- 返回：
  - transport：`streamable_http`
  - endpointPath：`/mcp`
  - draftOnly：`true`
  - requiresVaultUnlock：`true`
  - tools：name、group、summary
  - prompts：name、title、summary
- 不返回 Token、Token hash、密钥、Vault 路径或用户数据。

## 4. Web

新增 `/settings/mcp`：

- 从 Settings 页面进入。
- 展示使用当前 `window.location.origin` 生成的 `/mcp` 地址。
- 展示 Bearer 鉴权、Vault 解锁前提和 Draft-only 边界。
- 提供不含真实 Token 的 JSON 配置模板复制按钮。
- 复用现有 MCP Token 面板，用于生成或轮换 Token。
- 展示当前 Tools 和 Prompts 清单。

## 5. 验收标准

- WHEN 用户访问 `/settings/mcp` THEN 页面显示连接地址、鉴权方式、Draft-only 边界、Tools 和 Prompts。
- WHEN 用户尚未配置 Token THEN 页面仍可查看接入信息，并提示生成 Token。
- WHEN 能力目录变化 THEN Web 不维护第二份 Tools/Prompts 名称清单。
- WHEN 读取 `/api/mcp/info` THEN 不返回任何 Token 或秘密。
- WHEN 页面进行 Axe 检查 THEN 无 serious/critical 问题。

## 6. 非目标

- 不在浏览器中发起 MCP JSON-RPC 调用。
- 不保存或展示历史 MCP Token。
- 不新增 MCP 写入 Tool。
- 不新增聊天页、Agent Runtime 或外部客户端托管。
