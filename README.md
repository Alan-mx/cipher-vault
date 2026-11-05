# CipherVault V2

CipherVault 是本地优先、加密存储、由外部 AI 通过 MCP 使用的个人知识基础设施。
一个 ASP.NET Core 进程同时提供 Vue Web UI、REST API、MCP HTTP 端点，以及受
SQLite3MC 保护的本地数据库。

当前实现聚焦 v1.3 P0 闭环：

- 可配置 Knowledge Model：类型、属性、模板、关系和来源规则。
- 独立 Source 对象：来源版本、内容快照、章节/段落定位与全文读取。
- 正式 SourceReference：Knowledge 可追溯具体来源版本和定位。
- Keyword / Fulltext 搜索：不依赖 Embedding，不暴露 Semantic 或 Hybrid。
- MCP Draft-only：外部 AI 只能读取、搜索、创建/修改草稿并提交审核。
- Web 审核：正式 Knowledge、Source 和关系只能由用户批准后变更。

当前不建设内置 Ask AI、Chat LLM Provider、Agent Runtime、自有聊天历史、明文导入
导出、Semantic/Embedding/Hybrid 检索或 MCP Direct Write Mode。

## Windows

解压发布包后运行：

```powershell
.\Start-CipherVault.ps1
```

可选自动打开浏览器：

```powershell
.\Start-CipherVault.ps1 -OpenBrowser
```

默认地址：

```text
http://127.0.0.1:43127
```

默认用户数据目录：

```text
%LOCALAPPDATA%\CipherVault\
├── data\
├── config\
└── backups\
```

升级前停止 Server，再替换程序目录。不要删除 `%LOCALAPPDATA%\CipherVault`。

## Linux

x64：

```bash
tar -xzf CipherVault-2.0.0-rc.1-linux-x64.tar.gz
cd CipherVault-2.0.0-rc.1-linux-x64
```

ARM64：

```bash
tar -xzf CipherVault-2.0.0-rc.1-linux-arm64.tar.gz
cd CipherVault-2.0.0-rc.1-linux-arm64
```

Linux x64 基线为 Ubuntu 22.04 和 Debian 12。Linux ARM64 实机验证在后续功能完成后
通过目标设备统一执行。

systemd 安装需要 root：

```bash
sudo ./install-systemd.sh
systemctl status cipher-vault.service
```

安装后路径：

```text
/opt/cipher-vault                 程序
/var/lib/cipher-vault/data        Vault 数据库与 key envelope
/var/lib/cipher-vault/config      配置与 MCP Token
/var/lib/cipher-vault/backups     加密备份
```

卸载时重新执行发布包中的 `uninstall-systemd.sh`；Vault、配置和备份默认保留。

## Docker

```bash
docker compose -f v2/deploy/docker-compose.yml up -d
```

默认地址：

```text
http://127.0.0.1:8080
```

Compose 只发布宿主机回环地址，并使用命名 Volume `cipher-vault-data`。停止容器不会
删除数据；`docker compose down -v` 会永久删除 Vault、配置、MCP Token 和备份。

## Web 工作流

首次打开页面时设置 Vault Password，并确认“忘记密码不可恢复”。初始化后必须重新
Unlock。Vault 在进程退出、闲置超时或手动 Lock 后回到 Locked。

最小导航：

- 首页：最近知识、来源、待审核 Draft 和收集箱。
- 知识库：关键词/全文检索，以及 Knowledge 详情。
- 收集箱：记录 Knowledge Draft。
- 来源：创建 Source Draft，查看来源版本和内容。
- AI 工作区：把草稿提交审核，并批准、拒绝或丢弃 Draft。
- 知识网络：查看局部已确认关系。
- 知识模型：维护类型、属性、模板、关系和来源规则。
- 设置：修改密码、MCP 接入与 Token、知识模型和加密备份。

Knowledge 正文以 Markdown 为主，但类型、属性、标签和来源引用由数据库结构化字段
作为权威数据源。Frontmatter 只作为导入、导出和展示格式，并必须与结构化数据校验。

## MCP HTTP

MCP 地址：

```text
http://127.0.0.1:43127/mcp
```

Docker 默认宿主机地址：

```text
http://127.0.0.1:8080/mcp
```

在 Web Settings 中 Rotate MCP Token。Token 只显示一次。MCP 永久 Draft-only：

- 可以读取 Knowledge Model、Source、Source Revision、Source Locator 和 Knowledge。
- 可以搜索 Knowledge，查看局部关系，创建/修改/重绑/提交/丢弃 Draft。
- 不能批准、应用或拒绝 Draft。
- 不能直接写入正式 Knowledge、Source 或关系。
- 不提供 Direct Write Mode。
- 所有 MCP 副作用调用都要求 UUID `idempotencyKey`。

MCP Prompts 提供以下可复用工作流说明：

- `answer_with_knowledge`
- `extract_source_to_notes`
- `synthesize_selected_knowledge`
- `suggest_knowledge_relations`
- `review_knowledge_quality`
- `review_personal_progress`

Prompts 只返回工作流模板，不执行 Agent，也不产生正式写入；实际读取、搜索和 Draft
操作仍由外部 AI 通过 MCP Tools 完成。

Web 的 `设置 -> 打开 MCP 接入` 页面提供当前 MCP 地址、Bearer 鉴权模板、Draft-only
边界，以及服务端当前注册的 Tools 和 Prompts 清单。

Resource：

```text
cipher-vault://knowledge-model
```

不支持 Resources 的客户端应调用 `get_knowledge_model` Tool。

通用 HTTP 配置：

```json
{
  "transport": "http",
  "url": "http://127.0.0.1:43127/mcp",
  "headers": {
    "Authorization": "Bearer <token>"
  }
}
```

## 来源与知识

Source 是独立对象。统一模型使用 `SourceKind + metadata` 支持外部 URL、本地文件、
书籍、播客和其他来源。URL 只表示外部来源；需要保留内容时，保存独立 Source Revision
快照，确保后续可追溯。

SourceReference 包含：

- `sourceId`
- `sourceRevision`
- `locatorType`
- `locatorValue`
- `excerpt`
- `extractionMetadata`

章节和段落定位首版可用，页码与时间戳字段预留并按来源类型逐步启用。仍被正式
Knowledge 引用的 Source 不允许直接删除；必须先解除引用或保留归档记录。

## 搜索

Knowledge Library 和 MCP `search_knowledge` 支持：

- `keyword`：标题、摘要、标签、类型和结构化属性的轻量匹配。
- `fulltext`：SQLite FTS5 全文检索。

不提供 `semantic` 或 `hybrid`。不可用的检索能力不会通过隐藏模式或降级标签暴露。

## 数据库与恢复

v1.3 P0 使用单一新基线 Migration：

```text
v2/src/CipherVault.Core/Database/Migrations/001_mcp_first_baseline.sql
```

项目未上线，不兼容旧 Schema、旧测试数据库或旧备份格式。真实
`%LOCALAPPDATA%\CipherVault` 数据和用户备份不会作为构建产物清理。

在 Settings 中创建并下载 `.cvbackup`。备份包含加密数据库快照和 key envelope，
不包含明文正文、Vault Password、MCP Token 或 raw key。

恢复前必须停止 Server。恢复只支持交互式终端，目标必须是尚不存在的新目录：

```powershell
.\CipherVault.Server.exe `
  --restore-backup "C:\path\backup.cvbackup" `
  --target "C:\path\restored-vault"
```

Linux：

```bash
sudo systemctl stop cipher-vault.service

sudo -u cipher-vault \
  CIPHERVAULT_CONFIG_DIR=/var/lib/cipher-vault/config \
  CIPHERVAULT_BACKUP_DIR=/var/lib/cipher-vault/backups \
  /opt/cipher-vault/CipherVault.Server \
  --restore-backup /path/to/backup.cvbackup \
  --target /path/to/restored-vault
```

恢复后的 Vault 启动状态为 Locked。使用恢复数据目录启动 Server 后，用原 Vault
Password Unlock。

## 本地开发

后端：

```powershell
dotnet build CipherVault.slnx --no-restore
dotnet test CipherVault.slnx --no-build --no-restore
dotnet format CipherVault.slnx --verify-no-changes --no-restore
```

前端：

```powershell
cd v2/frontend
npm ci --no-audit --no-fund
npm run format:check
npm run typecheck
npm run lint
npm test
npm run build
npm run test:e2e
```

研发约束、Migration 规则、安全红线和提交约定见 `AGENTS.md`。
