# 验收报告（G5）

> 需求名：v1.3-baseline-convergence ｜ 日期：2026-11-06
> 代码基线：`4afeecce24ffda4c804a5926bf28d3ba00650ece`
> 验证提交：`485ef7f`
> 结论：本地与 GitHub Actions 全量验证通过

## 1. 验证环境

| 环境 | 用途 | 说明 |
|---|---|---|
| Windows 11 + .NET 10.0.402 + Node 24.21.0 | 后端、前端、Windows E2E | 本地完整验证 |
| WSL Ubuntu 24.04 x64 + Linux Node 24.22.2 | Linux E2E | Linux x64 Chromium，使用 Linux x64 自包含 Server |
| GitHub Actions run `42163597729` | 完整 CI | 全量任务成功 |

Linux 测试使用可再生成的 `v2/artifacts/e2e/` 临时运行时和 Chromium；未作为源码提交。

## 2. 验证命令与结果

| 命令 | 结果 |
|---|---|
| `dotnet build CipherVault.slnx --no-restore` | 通过，0 warning / 0 error |
| `dotnet test CipherVault.slnx --no-restore` | 通过，Core 92 / Application 106 / Server 183，共 381 |
| `dotnet format CipherVault.slnx --verify-no-changes --no-restore` | 通过 |
| `npm run format:check` | 通过 |
| `npm run typecheck` | 通过 |
| `npm run lint` | 通过 |
| `npm test` | 通过，16 files / 67 tests |
| `npm run build` | 通过 |
| Windows `npm run test:e2e` | 通过，14/14 |
| Linux `playwright test` | 通过，14/14，使用 `knowledge-library-linux.png` 正式比较 |
| GitHub Actions structure | 通过 |
| GitHub Actions frontend（含 Linux E2E） | 通过，14/14 |
| GitHub Actions dotnet-windows | 通过 |
| GitHub Actions dotnet-linux | 通过 |
| GitHub Actions dotnet-linux-arm64（含打包） | 通过 |
| GitHub Actions docker | 通过 |

### 2.1 已批准范围例外

- Migration SQL、Schema Version、Backup FormatVersion 和表结构未修改。
- `DatabaseMigrationCatalog` 将换行统一为 LF 后计算 checksum，并兼容白名单中的旧
  Windows checksum；该修复用于消除 Windows/Linux 平台差异，避免已有 Vault 被误判。
- 完整跨平台 CI 保持手动或正式发布触发；普通 push/PR 只运行快速检查，这是已确认的
  CI 资源策略。

## 3. AC 收敛矩阵

| AC | 结论 | 证据 / 差距 |
|---|---|---|
| AC1 | verified | 产品设计、spec、progress 均记录基线 SHA |
| AC2 | verified | 产品设计 6.1 删除固定五阶段，改为自定义属性 |
| AC3 | verified | 产品设计 6.2 只列三类规则和 Draft/Conflict 独立计数 |
| AC4 | verified | 产品设计与 README 均描述只读队列和 AI 任务复制 |
| AC5 | verified | 重复检测与观点验证明确标为当前范围外 |
| AC6 | verified | 产品设计 8.2 列出四种历史终态 |
| AC7 | verified | 产品设计说明 MCP 使用 Prompt + 通用 Draft |
| AC8 | verified | `mcp-first-v1.3/progress.md` 增加 T77 |
| AC9 | verified | 文档白名单已固化并按白名单提交 |
| AC10 | verified | 不使用 `git add docs/`；采用逐文件命令 |
| AC11 | verified | cached 清单无其他 `docs/` 工作产物 |
| AC12 | verified | AGENTS 引用的产品设计与 mcp-first-v1.3 路径均存在 |
| AC13 | verified | 前端单元测试覆盖默认 `pending_review` |
| AC14 | verified | 前端单元测试断言七项选项与固定顺序 |
| AC15 | verified | 单元测试覆盖 applied 精确查询 |
| AC16 | verified | 单元测试覆盖 rejected 精确查询 |
| AC17 | verified | 单元测试覆盖 discarded 精确查询 |
| AC18 | verified | 单元测试覆盖 expired 精确查询 |
| AC19 | verified | 四种终态详情均断言无写操作按钮 |
| AC20 | verified | 四种终态 URL 深链测试通过 |
| AC21 | verified | 非法 status 回退并归一 URL |
| AC22 | verified | 单元测试断言精确 wire value 传给现有 API |
| AC23 | verified | `git diff --name-only -- v2/src` 无 API 变更 |
| AC24 | verified | 四种终态空列表测试通过 |
| AC25 | verified | Linux PNG 来自 GitHub runner Actual，1280×720，正式比较通过；SHA256 `4713711E6343463DC3D31FE25670108FA8DD8B09ED0FF041AEAFBEF31845C561` |
| AC26 | verified | Windows PNG SHA256 未变：`530918FFC0B64CB78498C1AAA1BDB49CE3DBEB8190A9D6D320698C526D74A9D4` |
| AC27 | verified | `toHaveScreenshot("knowledge-library.png")` 保持不变 |
| AC28 | verified | GitHub Actions frontend 全量 E2E 14/14 通过 |
| AC29 | verified | 未用 Windows 结果替代 Linux；GitHub runner 使用 `knowledge-library-linux.png` 通过 |
| AC30 | verified | 未修改 `KnowledgeStatus` 或成熟度枚举/种子 |
| AC31 | verified | Triage 源码与视图无变更 |
| AC32 | verified（批准例外） | SQL Migration、Schema、Backup Format、表结构未变；checksum 规范化与旧 checksum 白名单为 G5 批准例外 |
| AC33 | verified | MCP 注册与契约无变更 |
| AC34 | verified | 未新增专用 MCP Tool 或其他范围外能力 |
| AC35 | verified | 后端、前端静态检查、单元、构建和 E2E 全部通过 |
| AC36 | verified | Windows、Linux、ARM64、Docker 的 GitHub Actions 结果均独立通过 |

## 4. 差距分类

无未收敛项。

## 5. 未执行 / 未验证

- GitHub Actions ARM64 job 已完成构建、测试、打包和脚本验收；未将 CI runner 结果表述为
  新的物理 ARM64 设备验收。
- 未直接打开或修改真实用户 Vault；迁移历史兼容通过代码白名单和定向测试验证。

## 6. 结论

本地实现与 GitHub Actions 全量验证均已收敛。G5 基线冻结以 `34e8272` 为文档与代码
基线；本次文档提交只补充已批准范围例外，不改变业务实现。
