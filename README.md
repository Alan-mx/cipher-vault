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
.\scripts\sign-publish.ps1 -SignerThumbprint 0123456789ABCDEF...
```

`sign-publish.ps1` 会签名发布目录中的五个 EXE，验证 Authenticode 签名，然后重新
创建最终 ZIP。所有客户端和 Host 必须由同一个代码签名证书签名。

## Service install

在管理员 PowerShell 中安装：

```powershell
cd "$env:ProgramFiles\CipherVault"
.\CipherVault.Cli.exe service install --signer-thumbprint 0123456789ABCDEF...
```

安装要求：

- `%ProgramData%\CipherVault` 不存在或为空。
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

`service uninstall` 只移除服务注册和签名安装清单，不删除 Vault 数据。再次执行
严格安装前必须显式确认并清理或备份旧安装数据；当前版本没有自动升级迁移。

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

## Restore

备份是加密的 `.cvbackup`。恢复到新目录不会替换当前服务 Vault：

```powershell
.\CipherVault.Cli.exe restore --backup D:\backups\vault.cvbackup --to D:\restored-vault
```

若要替换当前 Vault，必须先停止 Host，并显式将恢复结果放入服务 Vault 目录后再
启动。当前版本没有在线替换和自动迁移。

## License

MIT License. See [LICENSE](LICENSE).
