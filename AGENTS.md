# AGENTS.md

> 定位：CipherVault 本地加密知识库，当前活跃实现位于 `v2/`。
>
> 技术栈：.NET 10、ASP.NET Core、C#、SQLite3MC、xUnit、Vue 3、TypeScript、Vite、Playwright。
>
> 本文件只补充 CipherVault 项目特有规则；用户级和 `D:\workspace\AGENTS.md` 约定继续有效。本文件作为项目级规范随仓库维护。

## 1. 仓库边界

- 当前构建入口是仓库根目录 `CipherVault.slnx`。
- 活跃源码在 `v2/src`，测试在 `v2/tests`，前端在 `v2/frontend`。
- 根目录旧的 `src/`、`tests/`、`poc/` 是本地遗留内容，不是当前构建输入；不要在其上继续开发，除非用户明确要求考古或迁移。
- `artifacts/`、`TestResults/`、`v2/artifacts/`、`bin/`、`obj/` 是可再生成的构建/测试产物。
- `%LOCALAPPDATA%\CipherVault` 是真实应用数据目录，包含 Vault 数据库、key、备份和 token；不得将其当作构建产物清理。

## 2. 必须先查现状

处理需求或 bug 时，先搜索并阅读相关实现，不允许仅凭经验直接改代码。至少检查：

- `v2/src/CipherVault.Core` 的数据库、加密、存储实现
- `v2/src/CipherVault.Application` 的用例和事务边界
- `v2/src/CipherVault.Server` 的 API、Hosting、MCP 实现
- `v2/frontend` 的页面、组件、API client 和测试
- 相关 Migration SQL、备份、恢复、审计和权限测试
- 已存在的 `docs/specs`、`docs/bugs` 和 `README.md`

已有实现能复用时优先复用，不重新造第二套机制。

## 3. 分层与实现边界

- `Core` 保持存储、数据库、加密、领域模型能力，不引入 HTTP 依赖。
- `Application` 组织用例、事务和业务编排，不绕过 Core 的安全边界。
- `Server` 负责 HTTP/MCP/Hosting/Command，不直接把数据库细节泄漏给前端。
- 前端只通过现有 API contract 交互，不自行假设服务端字段或错误码。
- 新接口必须同时检查请求/响应 contract、错误映射、鉴权、CSRF、审计和相关测试。
- 不引入不必要的新框架、依赖、配置或抽象。

## 4. 数据库与 Migration

- 当前数据库 Schema 为 `5`，备份包 FormatVersion 为 `1`，最低备份兼容 Schema 为 `5`。
- 软件版本、数据库 Schema 和备份 FormatVersion 是独立版本体系；普通软件升级不要求 Schema 升级。
- 已发布 Migration SQL 不得修改、重命名、删除或重排；结构变化只能追加新 Migration。
- Migration 必须保持名称、顺序和 SHA256 可验证；`v2/tests/Fixtures/Migrations/migration-checksums.json` 是固定基线。
- 数据库打开/Migration 前必须保持 `vault_meta` 与 `schema_migrations` 一致性；不得在验证失败后继续写库。
- 不直接修改用户数据库；Migration 和恢复变更必须通过现有事务、校验和测试链路。
- 旧本地 Vault 可以按现有 Migration 升级；备份恢复只接受 `[MinimumSupportedSchemaVersion, CurrentVersion]`。
- 备份创建必须来自当前 Schema；恢复旧 Schema 后由首次正常解锁执行 Migration，不在恢复维护命令中另建迁移引擎。
- 已发布 Schema 5 的 `.cvbackup` 夹具必须保留来源信息并冻结，不得由后续版本自动重新生成覆盖。

## 5. 安全与恢复红线

- 不得降低 SQLite3MC 加密、key envelope、Argon2 密钥派生、Vault 锁定、CSRF、鉴权或完整性校验。
- 不得在日志、异常、测试输出或文档中写入 Vault Password、raw key、MCP Token、文档正文或用户备份内容。
- 备份恢复必须保留现有 staging、路径安全检查、哈希校验和 `Directory.Move` 发布；失败不得发布半成品。
- 真实加密备份测试应优先使用固定测试夹具；不得把真实用户数据加入仓库。
- 发现安全边界问题时先记录风险并征求用户意见，不通过关闭校验临时绕过。

## 6. 构建与测试

后端：

```powershell
dotnet build CipherVault.slnx --no-restore
dotnet test CipherVault.slnx --no-restore
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

要求：

- 代码修改后立即做最小相关构建/测试，再进入下一项。
- 不以“逻辑上应该可以”代替新鲜输出；最终报告必须附实际命令和结果。
- 默认验证优先级：
  1. Windows：运行后端、前端和与本改动相关的测试。
  2. Linux ARM64：通过 `ssh orbital@oneplus6` 连接目标设备执行实际验证。
- Linux x86/x64 本地测试不再作为必跑项；本地 Docker 测试也不再作为必跑项。
- CI 仍可能运行 Linux x64 或 Docker 作业，但本地 Agent 不要求主动补跑；本地未执行时不得声称通过。
- Linux ARM64 远程仓库路径和运行环境必须先确认，不得猜测；无法连接或测试失败时如实报告，不用 Windows 结果替代 ARM64 结果。

## 7. 编码与测试习惯

- 遵守现有 C# nullable、`TreatWarningsAsErrors`、`.editorconfig` 和最小修改原则。
- 不顺手重构无关模块，不无关格式化整个文件，不升级无关依赖。
- Core 公共 API 变更必须检查所有调用点、契约测试和文档。
- 业务数据测试优先使用真实 SQLite/SQLite3MC、真实加密包和真实恢复路径；Mock 只用于隔离外围 I/O。
- 备份、Migration、恢复和权限测试必须覆盖失败路径、回滚、清理和重试。
- 不新增无界查询、N+1 查询或未限制大小的读入。

## 8. Git 与文档

- 不得自行 commit / push；完成后先询问「是否提交」。
- 提交信息格式：`type: + 一句中文短标题`，约 10~25 字，不写实现细节和返工过程。
- SQL Migration、设计文档、接口对接文档、`docs/` 下工作产物默认不提交；只 add 具体源码文件。
- 已在 Git 中跟踪的 `README.md` / 在册文档，随业务更新后单独 add 提交。
- `AGENTS.md` 作为项目级协作规范随仓库维护，修改后需要同步评审。
- 不覆盖、回滚或删除用户已有的未提交修改。

## 9. 清理与数据保留

- 可以清理 `bin/`、`obj/`、`TestResults/`、`artifacts/`、`v2/artifacts/` 等可再生成产物，但执行前必须列出目标、总量和影响并取得用户确认。
- 根目录旧的 `src/`、`tests/`、`poc/` 只有确认不再需要旧版实现后才能清理；不得默认删除。
- 绝对禁止在未单独确认的情况下删除 `%LOCALAPPDATA%\CipherVault`、真实 `.cvbackup`、`vault.db`、`vault.key` 或用户备份。
- 清理前先确认是否存在正在运行的 CipherVault 进程、未备份的真实 Vault 和用户需要的旧发布包。

## 10. 标准任务流程

新需求开发或 bug 修复统一走 `dev-flow`：

```text
需求澄清
    -> 规格
    -> 先设计数据结构
    -> 再设计接口
    -> 逐接口垂直实现
    -> 收敛验证
    -> 询问是否提交
```

完成后至少汇报：改动文件、验证命令与结果、未完成项、风险和是否需要提交。
