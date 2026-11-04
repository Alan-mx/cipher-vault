# CipherVault V2

版本：`2.0.0-rc.1`

CipherVault 是一个跨平台、单用户、本地优先的加密 Markdown/TXT 知识库。一个
ASP.NET Core 进程同时提供 Vue Web UI、REST API、MCP HTTP 端点和加密
SQLite3MC 数据库。

当前支持：

- Windows 11 x64，self-contained ZIP。
- glibc Linux x64 与 ARM64，tar.gz 与 systemd。
- linux/amd64 Docker 镜像与 Compose。

当前不支持 musl/Alpine、MSI、deb、rpm、AppImage、自动更新或公网直连。

## Windows

解压 `CipherVault-2.0.0-rc.1-win-x64.zip`，在解压目录运行：

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

登录后自动启动对当前用户可选，不需要管理员权限：

```powershell
.\Install-CipherVaultStartup.ps1
```

移除登录启动：

```powershell
.\Install-CipherVaultStartup.ps1 -Uninstall
```

默认用户数据目录：

```text
%LOCALAPPDATA%\CipherVault\
├── data\
├── config\
└── backups\
```

升级前停止 Server，再替换解压后的程序目录。不要删除
`%LOCALAPPDATA%\CipherVault`。

## Linux

按机器架构选择对应压缩包。x64：

```bash
tar -xzf CipherVault-2.0.0-rc.1-linux-x64.tar.gz
cd CipherVault-2.0.0-rc.1-linux-x64
```

ARM64：

```bash
tar -xzf CipherVault-2.0.0-rc.1-linux-arm64.tar.gz
cd CipherVault-2.0.0-rc.1-linux-arm64
```

Linux x64 基线为 Ubuntu 22.04 和 Debian 12。Linux ARM64 的自动化基线为
Ubuntu 24.04，OnePlus 6T 上的 Ubuntu 26.04.1 为待补实机目标；其他 ARM64
发行版暂不承诺支持。

systemd 安装需要 root：

```bash
sudo ./install-systemd.sh
```

默认地址：

```text
http://127.0.0.1:43127
```

检查状态：

```bash
systemctl status cipher-vault.service
journalctl -u cipher-vault.service
```

不安装 systemd 时可直接前台运行：

```bash
cd publish
./CipherVault.Server
```

安装后路径：

```text
/opt/cipher-vault                 程序
/var/lib/cipher-vault/data        Vault 数据库与 key envelope
/var/lib/cipher-vault/config      配置与 MCP Token
/var/lib/cipher-vault/backups     加密备份
```

升级时重新执行 `install-systemd.sh`。脚本替换程序并保留
`/var/lib/cipher-vault`。

卸载：

```bash
sudo ./uninstall-systemd.sh
```

卸载删除 systemd unit 与程序，保留 Vault、配置、备份和 `cipher-vault` 用户。

## Docker

加载镜像：

```bash
docker load \
  --input cipher-vault-2.0.0-rc.1-docker-linux-amd64.tar
```

使用仓库中的 Compose：

```bash
docker compose -f v2/deploy/docker-compose.yml up -d
```

默认地址：

```text
http://127.0.0.1:8080
```

Compose 只发布宿主机 `127.0.0.1`，使用命名 Volume `cipher-vault-data`：

```text
/data/vault
/data/config
/data/backups
```

停止并删除容器，保留 Volume：

```bash
docker compose -f v2/deploy/docker-compose.yml down
```

删除命名 Volume 会永久删除 Vault、配置、MCP Token 和备份：

```bash
docker compose -f v2/deploy/docker-compose.yml down -v
```

升级时加载新镜像并重建容器。不要删除或替换该命名 Volume。

## Web 初始化与解锁

第一次打开页面时：

1. 设置 Vault Password。
2. 明确确认“忘记密码不可恢复”。
3. 初始化后重新 Unlock。

Vault 在进程退出、闲置超时或手动 Lock 后回到 Locked，但进程本身仍可继续提供状态页。

## MCP HTTP

MCP 地址：

```text
http://127.0.0.1:43127/mcp
```

Docker 默认宿主机地址：

```text
http://127.0.0.1:8080/mcp
```

在 Web Settings 中 Rotate MCP Token。Token 只显示一次。MCP 默认使用草稿模式：
AI 可以创建、修改和送审加密草稿，但不能直接修改正式知识，也不能批准或提交草稿。
用户可在 Settings 中显式开启当前 Vault Session 的 Direct Write Mode。开启后，
所有持有当前 MCP Token 的客户端都可直接写入正式文档。任何 MCP 模式都不会绕过
Vault Lock；Token 不是 Vault Password，也不能替代 Web Unlock。

所有 MCP 副作用调用都要求 UUID `idempotencyKey`；正式写入工具在草稿模式下仍会
出现在工具清单中，但调用会返回结构化错误 `write_mode_draft_required`。

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

## 搜索与 Frontmatter 属性过滤

Web SearchDialog 和 MCP `search_notes` 支持结构化 Frontmatter 属性过滤，例如
`status=verified`、`project=CipherVault` 或 `source.kind=project`。

- 关键词可以为空，但必须至少提供一个标签、格式、时间、链接、孤立或属性过滤条件。
- 属性条件支持 `exists/eq/contains/in/gte/lte`，最多 16 条，组内支持 `all/any`。
- 字段路径最多 4 层；顶层 `tags` 继续使用现有标签过滤。
- 属性查询使用 SQLite JSON1，不新增属性索引；纯过滤查询可能扫描候选文档。
- Web 使用 `POST /api/search/query`；旧 `GET /api/search` 简单搜索合同保留。
- MCP `search_notes` 增加 `propertyFilters` 和 `propertyMatch` 参数。

## Markdown 相对链接

正式知识关系同时支持 WikiLink 和 inline Markdown 相对链接：

- 支持 `[文字](../folder/Note.md#标题)`、根路径 `/folder/Note.md` 和可选链接 title。
- 仅索引 `.md` / `.txt`；外部 URL、图片、reference-style link、代码块和 HTML 注释不建立关系。
- 路径按源文档目录解析，禁止越出 Vault 根目录；percent encoding 严格按 UTF-8 解码一次。
- 标题和路径变化时自动在草稿或正式写入的同一事务内更新反向 WikiLink 和相对链接。
- unresolved / ambiguous 链接在 Web 中只显示文本，不跳转到错误的站点路径。

## 稳定文档 ID 与迁移

- Markdown 的顶层 Frontmatter `id` 与数据库 `documentId` 是同一个 GUID。
- 新建、更新、草稿提交和 Revision Restore 由服务端统一维护 ID，普通编辑和 MCP 不能替换身份。
- 旧 Markdown 在下一次正式写入时补充 `id`；导出时只在导出副本中补充，不修改数据库原文。
- TXT 正文不写身份标记，导出 Manifest v2 保存其 `documentId`。
- 导入会恢复 Markdown/Manifest 身份；重复、已占用或回收站中的 ID 不会覆盖已有文档。
- 文档页支持“复制”，新文档使用全新 ID、Revision 1，并重新建立出链。

## 明文导出

Settings 页面提供“导出明文”。该操作会下载当前全部有效 Markdown/TXT 文档的未加密 ZIP：

- 保持 Vault 中的虚拟目录和 `.md` / `.txt` 路径。
- 保留 Frontmatter、标签和标准 Markdown 链接；Markdown 会携带稳定 `id`，已解析 WikiLink 会转换为相对 Markdown 链接。
- Manifest 使用 FormatVersion 2，包含每篇文档的 `documentId`、标题、路径、大小和 SHA-256。
- 不包含回收站、历史 Revision、Audit、备份或配置。
- ZIP 内包含 `_cipher-vault-export.json` 清单和逐文件 SHA-256。
- 不新增导出密码；下载后文件完全脱离 Vault 加密保护。
- 用户必须输入确认短语 `导出明文` 并再次确认。
- MCP 和 AI 无权触发或读取全部明文导出。
- 服务端直接流式返回 ZIP，不保存明文临时文件。

明文导出要求 Vault 已解锁。导出期间文档写入会等待，以保证 ZIP 内容来自一致 revision。
当前限制为最多 10,000 篇、未压缩正文总量最多 1 GiB。

## 加密备份与离线恢复

在 Settings 中创建并下载 `.cvbackup`。备份包含加密数据库快照和 key envelope，不包含
明文正文、Vault Password、MCP Token 或 raw key。

恢复前必须停止 Server。恢复只支持交互式终端，目标必须是尚不存在的新目录。

Windows：

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

Docker：

```bash
docker run --rm -it \
  --volume /host/backups:/backup:ro \
  --volume /host/restore-parent:/restore \
  cipher-vault:2.0.0-rc.1 \
  --restore-backup /backup/backup.cvbackup \
  --target /restore/restored-vault
```

命令会提示 `Backup password:`。恢复后的 Vault 启动状态为 Locked。使用恢复数据目录启动
Server 后，用原 Vault Password Unlock。

### 自动 Git 加密备份

该功能默认关闭。启用后，文档修改会触发完整 `.cvbackup` 快照，合并 5 分钟内的连续修改，
并自动 commit、push 到独立私有 Git 仓库中的固定文件 `latest.cvbackup`。

要求：

- 使用独立私有仓库，不要直接使用本源码仓库。
- 本地仓库目录、remote 和 branch 必须预先存在。
- Git 凭据必须已由 Git Credential Manager、SSH Agent 或环境配置完成；后台不会弹出交互提示。
- Git 中保存的是完整加密快照。Git 历史会持续增长，不会自动重写或删除旧版本。
- 仓库达到 1 GiB 或 12 个月后进入 `rotation_required`，需要配置新的空仓库并重启。

配置示例：

```text
CIPHERVAULT_GIT_BACKUP_ENABLED = true
CIPHERVAULT_GIT_BACKUP_REPO = "D:\CipherVaultBackupRepo"
CIPHERVAULT_GIT_BACKUP_REMOTE = "origin"
CIPHERVAULT_GIT_BACKUP_BRANCH = "main"
```

完整配置：

| 变量 | 默认 | 说明 |
|---|---|---|
| `CIPHERVAULT_GIT_BACKUP_ENABLED` | `false` | 自动 Git 备份开关 |
| `CIPHERVAULT_GIT_BACKUP_REPO` | 无 | 已存在的独立 Git 工作树绝对路径 |
| `CIPHERVAULT_GIT_BACKUP_REMOTE` | `origin` | 已配置的 Git remote |
| `CIPHERVAULT_GIT_BACKUP_BRANCH` | `main` | 已 checkout 的目标分支 |
| `CIPHERVAULT_GIT_BACKUP_DEBOUNCE_SECONDS` | `300` | 最后一次修改后的等待时间 |
| `CIPHERVAULT_GIT_BACKUP_MAX_DELAY_SECONDS` | `1800` | 连续修改最长等待时间 |
| `CIPHERVAULT_GIT_BACKUP_RETRY_SECONDS` | `60` | 失败基础重试间隔 |
| `CIPHERVAULT_GIT_BACKUP_ROTATE_BYTES` | `1073741824` | Git 对象轮换阈值 |
| `CIPHERVAULT_GIT_BACKUP_ROTATE_DAYS` | `365` | 仓库最长使用天数 |

Windows 在当前用户环境变量中设置后，需要完整退出并重新启动 Server：

```powershell
[Environment]::SetEnvironmentVariable(
  "CIPHERVAULT_GIT_BACKUP_ENABLED", "true", "User")
[Environment]::SetEnvironmentVariable(
  "CIPHERVAULT_GIT_BACKUP_REPO", "D:\CipherVaultBackupRepo", "User")
```

Settings 页面会显示同步状态、最近 commit、错误码和轮换提示，不会显示仓库绝对路径或 remote
URL。自动同步状态保存在 `%LOCALAPPDATA%\CipherVault\config\git-backup-state.json`。

Docker 镜像已包含 Git 与 OpenSSH client。启用时需把独立备份仓库挂载为可写目录，并提供
容器用户可用的 Git 凭据；当前 Compose 默认不启用该功能。

## 升级与卸载

- Windows：停止 Server，替换程序目录，保留 `%LOCALAPPDATA%\CipherVault`。
- Linux：重新执行 `install-systemd.sh`，保留 `/var/lib/cipher-vault`。
- Docker：加载新镜像并重建容器，保留 `cipher-vault-data`。
- 不提供自动更新器。
- 不兼容旧 SQLCipher 数据库、旧 Recovery Key、旧配置文件，以及 Schema 低于 `5` 的 `.cvbackup`。
- 当前数据库 Schema 为 `6`，备份包 FormatVersion 为 `1`，最低备份兼容 Schema 为 `5`。
- 软件版本和数据库 Schema 版本相互独立；普通软件升级不要求 Schema 升级，只有结构或持久化格式确实变化时才追加 Migration。
- 本次版本及以后生成的、Schema 在兼容范围内的有效 `.cvbackup` 可由后续软件版本恢复，并在首次解锁时通过现有 Migration 升级。
- 已有本地 Schema 1~4 Vault 仍可通过正常升级和 Migration 链路升级到当前 Schema；该本地升级能力与备份最低兼容基线相互独立。

## 安全边界

- Vault Password 不可恢复。忘记密码会让 Vault 和已下载加密备份都不可恢复。
- MCP Token 与 Vault Password 分离。
- 默认只监听 loopback，不应把无 TLS 的端口直接暴露到公网。
- 远程使用时由用户自行配置 Tailscale、SSH 隧道或启用 HTTPS 的反向代理。
- 日志仅使用 Windows 控制台、systemd journal 或 Docker logs，不写文件日志。
- 当前 RC 不提供 Authenticode、Linux 包签名或 Docker 镜像签名。
- 不支持多用户、OAuth、自动更新或 Alpine/musl。

## 发布完整性

发布目录包含：

```text
release-manifest.json
SHA256SUMS
CipherVault-2.0.0-rc.1-win-x64.zip
CipherVault-2.0.0-rc.1-linux-x64.tar.gz
CipherVault-2.0.0-rc.1-linux-arm64.tar.gz
cipher-vault-2.0.0-rc.1-docker-linux-amd64.tar
```

Linux/WSL/macOS 校验：

```bash
sha256sum -c SHA256SUMS
```

Windows 可逐文件比较：

```powershell
Get-FileHash .\CipherVault-2.0.0-rc.1-win-x64.zip -Algorithm SHA256
```

SHA-256 用于发现损坏或篡改，不代表发布者身份认证，也不替代代码签名。

## License

见根目录 `LICENSE`。
