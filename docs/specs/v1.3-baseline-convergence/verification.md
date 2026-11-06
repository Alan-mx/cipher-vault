# 验收报告（G5）

> 需求名：v1.3-baseline-convergence ｜ 日期：2026-11-06
> 代码基线：`4afeecce24ffda4c804a5926bf28d3ba00650ece`
> 结论：本地 Windows 与 Linux 验证通过；GitHub Actions 待提交后触发，当前不能声称远端 CI 已通过

## 1. 验证环境

| 环境 | 用途 | 说明 |
|---|---|---|
| Windows 11 + .NET 10.0.402 + Node 24.21.0 | 后端、前端、Windows E2E | 本地完整验证 |
| WSL Ubuntu 24.04 x64 + Linux Node 24.22.2 | Linux E2E | Linux x64 Chromium，使用 Linux x64 自包含 Server |
| GitHub Actions ubuntu-latest | 正式 CI | 尚未触发 |

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
| AC9 | verified | 文档白名单已固化，待按白名单暂存 |
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
| AC25 | verified | Linux PNG 已生成，1280×720，正式比较通过 |
| AC26 | verified | Windows PNG SHA256 未变：`530918FFC0B64CB78498C1AAA1BDB49CE3DBEB8190A9D6D320698C526D74A9D4` |
| AC27 | verified | `toHaveScreenshot("knowledge-library.png")` 保持不变 |
| AC28 | partial | 本地 Ubuntu 24.04 全量 E2E 14/14 通过；GitHub Actions 尚未触发 |
| AC29 | partial | 已明确没有用 Windows 结果替代 Linux；远端 CI 仍待外部触发 |
| AC30 | verified | 未修改 `KnowledgeStatus` 或成熟度枚举/种子 |
| AC31 | verified | Triage 源码与视图无变更 |
| AC32 | verified | Migration、Schema、Backup Format 无变更 |
| AC33 | verified | MCP 注册与契约无变更 |
| AC34 | verified | 未新增专用 MCP Tool 或其他范围外能力 |
| AC35 | verified | 后端、前端静态检查、单元、构建和 E2E 全部通过 |
| AC36 | partial | Windows 与 Linux 本地证据已分开；GitHub CI 结果待提交后补录 |

## 4. 差距分类

| 类型 | 项目 | 说明 |
|---|---|---|
| partial | AC28 | 缺 GitHub Actions 远端运行结果 |
| partial | AC29 | 同上，不能把本地 WSL 结果写成 CI 已验证 |
| partial | AC36 | 同上，最终报告需补远端 CI 状态 |

## 5. 未执行 / 未验证

- 未 commit、未 push，因此 GitHub Actions 未触发。
- 未执行 Linux ARM64 本轮回归；本需求不涉及平台运行时或原生依赖，且本地 Linux x64
  已覆盖截图与页面行为。不得将本次结果表述为新的 ARM64 验收。
- 未修改或验证真实用户 Vault 数据；本轮不涉及数据库、备份或恢复。

## 6. 结论

本地实现与本地跨平台 E2E 已收敛。剩余差距只有远端 GitHub Actions 结果，需要获得用户
授权提交并推送后才能补齐。
