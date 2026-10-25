# CipherVault

Windows 11 本地加密 Markdown/TXT 资料库。当前版本使用 strict Windows Service
架构；不再维护旧的 per-user Host、兼容 HTTP Admin/MCP 或旧版升级迁移。

## Architecture

```text
CipherVault.Ui.Wpf / CipherVault.Cli / CipherVault.Mcp
                         |
                  verified named pipe
                         |
                  CipherVault.Host.exe（Windows Service，Session 0）
                         |
              CipherVault.Core + SQLCipher
                         |
                  CipherVault.Auth.Broker.exe
                         |
              密码/恢复密钥、审批、文件选择与复制
```

- `CipherVault.Host.exe` 以独立服务账户运行，是唯一持有 SQLCipher 连接、主密钥、
  锁定策略和审计流的位置。
- `CipherVault.Auth.Broker.exe` 运行在当前用户 session，负责所有需要用户交互的
  授权和文件操作。服务不弹窗，也不直接读取用户任意目录。
- WPF、CLI 和 MCP 都只通过签名镜像和命名管道身份验证访问 Host。
- strict 模式关闭 direct HTTP Admin API 和 direct HTTP MCP。
- Vault 数据和运行目录固定在 `%ProgramData%\CipherVault`。
- 程序和所有客户端映像固定在 `%ProgramFiles%\CipherVault`。

## Security model

- 文档内容、元数据、版本、回收站、FTS 和审计记录由 SQLCipher 加密。
- 密码和离线恢复密钥分别解封随机主密钥。
- 恢复密钥必须确认后才能访问文档内容。
- Vault 会话在空闲、硬超时、锁屏或用户显式锁定后锁定。
- 锁定会撤销 Admin session、Agent Access Grant 和待处理的 Approval。
- Agent 默认使用 Access Grant；长期 token 只能申请短期 Grant。
- Grant scopes 是 `read`、`write` 和 `delete`；删除必须显式批准。
- MCP strict Bridge 通过本机命名管道申请 Grant，并在进程退出时立即撤销。
- 永久删除、备份、导出、导入和恢复由一次性 Approval 绑定 OperationId、nonce、
  参数摘要、进程 session、Vault session 和过期时间。
- 文件操作只允许通过受控 staging 完成跨账户交接。

管理员、内核攻击、父进程注入、键盘记录和用户主动批准恶意进程仍超出保护范围。

## Build and test

```powershell
dotnet build .\CipherVault.sln -c Release --nologo
dotnet test .\CipherVault.sln -c Release --no-build --nologo
```

## Publish and sign

发布产物只是 staging 目录和 ZIP，不是可直接安装的 strict 发布包；安装前必须完成
代码签名：

```powershell
.\scripts\publish.ps1
.\scripts\sign-publish.ps1 -SignerThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
```

`sign-publish.ps1` 会签名发布目录中的五个 EXE，验证 Authenticode 签名，然后重新
创建最终 ZIP。参数是证书在 `CurrentUser\My` 中的 40 位 SHA-1 thumbprint；脚本优先
使用 `signtool.exe`，未安装 Windows SDK 时回退到 PowerShell Authenticode 签名。
所有客户端和 Host 必须由同一个代码签名证书签名。

## Service install

在管理员 PowerShell 中安装：

```powershell
cd "$env:ProgramFiles\CipherVault"
.\CipherVault.Cli.exe service install --signer-thumbprint <64位SHA-256签名者指纹>
```

安装要求：

- `%ProgramData%\CipherVault` 不存在、为空，或是 Owner/布局一致的完整 strict 安装。
- 五个 EXE 已签名且位于 `%ProgramFiles%\CipherVault`。
- 签名者 thumbprint 与安装参数一致。

安装器会创建 Windows Service、解析 `NT SERVICE\CipherVaultHost` SID、设置
ProgramData ACL，并生成只读的 `install-public.json`。

常用命令：

```powershell
.\CipherVault.Cli.exe service status
.\CipherVault.Cli.exe service start
.\CipherVault.Cli.exe service stop
.\CipherVault.Cli.exe service uninstall
```

`service uninstall` 只移除服务注册，不删除 `%ProgramData%\CipherVault` 或安装根。
在 Owner、服务账户和固定目录布局一致的完整 strict 安装上，可以直接重新安装；
安装器会生成新的 install ID 和安装清单并保留 Vault 数据。不完整、被篡改或包含
reparse point 的 ProgramData 布局会被拒绝，不会被当作旧数据自动迁移。

管理员端到端冒烟：

```powershell
.\scripts\strict-service-smoke.ps1 -SignerThumbprint <64位SHA-256签名者指纹>
```

脚本验证签名、SCM 启停、ProgramData Owner/ACL、Host/CLI/Broker/UI/MCP 进程身份，
并执行卸载后重装。默认保留数据；只有显式传入 `-RemoveData` 才删除
`%ProgramData%\CipherVault`。

## First use

1. 启动 `CipherVault.Ui.Wpf.exe` 或执行 `.\CipherVault.Cli.exe status`。
2. Broker 会弹出初始化向导；输入密码并保存离线恢复密钥。
3. 重新输入恢复密钥确认保存。
4. 之后使用密码或恢复密钥解锁。

密码和恢复密钥只由当前用户 session 的 Broker 收集，通过已验证命名管道传给 Host。
不要把它们写入配置、环境变量、聊天记录或代码。

## Desktop and CLI

WPF 提供日常读写、回收站、版本、备份、导出、导入和审计页面。CLI 提供相同能力的
命令行版本：

```powershell
.\CipherVault.Cli.exe status
.\CipherVault.Cli.exe search --query "invoice"
.\CipherVault.Cli.exe backup --to D:\backups\vault.cvbackup
.\CipherVault.Cli.exe export --to D:\backups\plaintext
.\CipherVault.Cli.exe host-stop
```

`host-stop` 会先锁定 Vault，再触发 Windows Service 停止。

## Knowledge network

Markdown 文档支持文档级 Wiki Link：

```markdown
[[目标文档]]
[[目标文档|显示名]]
[[目标文档#标题]]
```

WPF 的文档工作区提供：

- 安全 Markdown 预览和可点击内部链接。
- 输入 `[[` 时按标题和路径联想活动文档，支持键盘选择。
- 纯文本编辑、条件保存和 revision 冲突保护。
- 编辑标题时可同步把匹配的旧标题引用改写到新标题。
- 已解析反向链接、未解析链接和歧义候选。
- 1～2 跳局部关系图，支持标签 AND 和文件夹过滤。

链接关系与正文、版本、标签和 FTS 在同一个 SQLCipher 事务中更新。项目尚未上线，
schema 直接使用当前规范结构；旧开发 Vault 需要重新初始化。

Markdown 存在 Frontmatter `tags` 字段时，该字段是标签索引的权威来源。创建、正文更新、
追加和精准修改会在同一事务内同步 `tags_json`、标签关系、FTS、HMAC 和 revision；
同时传入的显式 `tags` 必须与 Frontmatter 一致。

完整性检查会继续比对当前文档和历史 revision 的 Frontmatter 标签、`tags_json`、
`document_tags`，并逐行比对 FTS 的标题、路径、标题树、标签和正文副本；缺失行、
额外行和字段漂移都会报告具体文档或 revision 的问题码。

重命名并同步引用时，目标文档写 `update` 汇总事件，每个被自动改写的来源文档写
`backlink_rewrite` 事件；同一事务内的事件共享 `correlationId`，可追溯到具体来源、
旧标题和新标题。

维护页可以全量重建链接索引；操作在单个 SQLCipher 事务内完成并写入审计。需要建立
本地性能基线时可运行：

```powershell
.\scripts\benchmark-knowledge-graph.ps1 -Documents 1000 -LinksPerDocument 10
```

批量导入按每 100 篇共享事务，并用单文件 SAVEPOINT 隔离失败；同批中单个文件变化或
冲突不会回滚其他已成功文件。链接重解析在每个批次末尾合并执行一次。导入性能基线：

```powershell
.\scripts\benchmark-import.ps1 -Documents 1000 -LinksPerDocument 10
```

精准修改支持按完整标题路径或顶层 frontmatter 字段执行 `append`、`prepend`、
`replace` 和 `delete`。dry-run 只返回 diff，不写入正文。

## MCP strict bridge

MCP 客户端使用 `CipherVault.Mcp.exe`：

```toml
[mcp_servers.cipher_vault]
command = 'C:\Program Files\CipherVault\CipherVault.Mcp.exe'
args = ['--scopes', 'read,write']
tool_timeout_sec = 120
default_tools_approval_mode = "prompt"
```

Bridge 启动后会验证 Host 和自身进程身份，请求用户批准 Access Grant，然后在进程
内存中持有短期 Grant。Bridge 退出或 Vault 锁定会立即撤销 Grant。

`--scopes read` 请求只读；`--scopes read,write` 请求读写；`delete` 必须显式加入
scope。Agent 不接受长期 Bearer token，也不使用 direct HTTP MCP。

当前 MCP 工具：

- 读取：`vault_status`、`list_catalog`、`search_notes`、`read_note`、
  `scan_notes`、`tag_list`、`get_document_map`、`get_backlinks`、
  `get_local_graph`。
- 写入：`create_note`、`append_note`、`update_note`、`patch_note`。
- 删除：`delete_note`，语义为移入加密回收站。

`update_note` 的 `updateBacklinks` 默认关闭；需要同步改写标题引用时显式设为 `true`。

## Restore

备份是加密的 `.cvbackup`。恢复到新目录不会替换当前服务 Vault：

```powershell
.\CipherVault.Cli.exe restore --backup D:\backups\vault.cvbackup --to D:\restored-vault
```

若要替换当前 Vault，必须先停止 Host，并显式将恢复结果放入服务 Vault 目录后再
启动。当前版本没有在线替换和自动迁移。

## 参考项目与借鉴边界

> 版本快照：2026-10-24。以下项目仅用于产品或架构参考，不构成当前运行时依赖。
> AGPL 项目只阅读其公开设计，不复制源码。具体规划见
> [`docs/specs/knowledge-graph-mcp/clarify.md`](docs/specs/knowledge-graph-mcp/clarify.md)。

| 项目 | 参考版本 | 许可/类型 | 借鉴内容 | 明确不照搬 |
| --- | --- | --- | --- | --- |
| [Obsidian](https://obsidian.md/) | 闭源产品，不锁定版本 | 闭源专有 | `[[笔记名]]`、反向链接、局部图谱、以标签过滤知识网络 | 把文件夹或全库图谱作为唯一主导航 |
| [Local REST API](https://github.com/coddingtonbear/obsidian-local-rest-api) | `5.5.2` | MIT | MCP 活集成、按标题/块/frontmatter 精准修改、条件写、当前文档上下文 | 回环 HTTP、Bearer Token 直连、任意命令执行；CipherVault 继续使用签名命名管道和短期 Grant |
| [Markdig](https://github.com/xoofx/markdig) | `1.4.2` | BSD-2-Clause | 安全的 Markdown AST 解析，用于 WPF 原生预览和链接注解 | 执行原始 HTML、脚本或远程资源 |
| [Basic Memory](https://github.com/basicmachines-co/basic-memory) | `v0.24.1` | AGPL-3.0 | Markdown 与 SQLite 索引并存、MCP 原生知识图谱、稳定 permalink、工具行为标注、文件/索引一致性检查 | 明文知识库、外部 Python 进程、云端记忆服务 |
| [Logseq](https://github.com/logseq/logseq) | `2.0.2`（DB beta） | AGPL-3.0 | 页面/块级引用、属性查询、图谱过滤思路；为未来块引用预留模型 | 直接采用 beta DB 实现，或把全库图谱作为主要入口 |
| [SiYuan](https://github.com/siyuan-note/siyuan) | `v3.8.8` | AGPL-3.0 | 稳定块 ID、块级双向链接、文档地图、CLI/API、`--dry-run` 预览修改 | 向 Agent 暴露任意 SQL；首版直接实现块级模型 |
| [TriliumNext](https://github.com/TriliumNext/Trilium) | `v0.106.1` | AGPL-3.0 | 关系属性、Relation Map/Note Map、节点克隆、大规模个人知识库设计 | 树结构优先的产品模型；复制 AGPL 代码或首版实现克隆 |
| [SilverBullet](https://github.com/silverbulletmd/silverbullet) | `2.12.1` | MIT | Wiki 双向链接与数据库查询结合、用可配置视图组织知识 | Lua 脚本运行时、浏览器/服务端架构 |
| [MCP Memory Service](https://github.com/doobidoo/mcp-memory-service) | `v12.3.1` | Apache-2.0 | 本地优先检索、Agent 作用域、实时变更通知、图谱仪表盘 | 首版引入自主记忆整合、向量数据库和云端同步 |
| [MCP Memory Server](https://github.com/modelcontextprotocol/servers/tree/main/src/memory) | npm `2.0.0` | MIT | Entity/Relation/Observation 模型、MCP Resource 变更通知 | JSONL 单文件存储、直接返回整张图谱 |
| [MCP Security Best Practices](https://modelcontextprotocol.io/specification/latest/basic/security_best_practices) | `2026-07-28` | 规范文本 | 本地 MCP Server 风险、权限最小化、用户同意、工具行为提示 | 不把工具提示当作授权依据；安全约束仍由 Host 强制执行 |
| [MSAGL](https://github.com/microsoft/automatic-graph-layout) | `Msagl.WpfGraphControl 1.2.1` | MIT | 原生 WPF 图谱布局、缩放、节点搜索和子图折叠 | 首版用它承载全库大图谱 |
| [Cytoscape.js](https://github.com/cytoscape/cytoscape.js) | `3.34.4` | MIT | 成熟的关系图交互、筛选和扩展生态 | 首版引入 WebView2 和浏览器渲染栈 |
| [Sigma.js](https://github.com/jacomyal/sigma.js) + [Graphology](https://github.com/graphology/graphology) | `4.0.3` + `0.26.0` | MIT | WebGL 大图谱渲染和图层分析 | 首版使用；仅在全库图谱阶段重新评估 |

社区经验用于校验优先级：

- [Logseq: Graph performance](https://discuss.logseq.com/t/graph-performance/34524)
  记录约 2190 个 Markdown 文件时图谱交互明显退化，因此 CipherVault 首版限制
  局部图节点数，不把全库图谱作为主要浏览方式。
- [Logseq: filtered views of the graph](https://discuss.logseq.com/t/presentation-mode-filtered-views-of-the-graph/22431)
  讨论表明用户更需要按标签、属性和上下文筛选的子图。

## License

MIT License. See [LICENSE](LICENSE).
