# 实施进度台账

> 需求名：v1.3-baseline-convergence ｜ 更新：2026-11-06

| 任务 | 状态 | 验证证据 / 裁决 |
|---|---|---|
| T1 扩展 AI 工作区历史筛选 | 完成 | `npm test -- draft-review-editing` 14/14；`npm run typecheck`、`npm run lint`、`npm run format:check` 通过 |
| T2 增加 Linux 截图基准 | 完成 | GitHub Actions frontend 全量 E2E 14/14；`knowledge-library-linux.png` 采用 CI Actual，1280×720，SHA256 `4713711E6343463DC3D31FE25670108FA8DD8B09ED0FF041AEAFBEF31845C561` 校验通过；Windows SHA256 未变 |
| T3 收敛产品与验收文档 | 完成 | 产品设计已移除固定成熟度默认表、补齐三类只读整理规则与四种历史终态；P0 progress 增加 T77；设计旧架构路径依赖已移除 |
| T4 文档 Git 白名单落地 | 完成 | `git diff --cached --name-only` 仅包含指定产品设计、P0 证据、convergence 文档与本次前端源码/测试；未暂存其他 `docs/` 目录 |
| T5 非回归与收敛验证 | 完成 | GitHub Actions run `42163597729`：structure、frontend（E2E 14/14）、dotnet-windows、dotnet-linux、dotnet-linux-arm64、docker 全部成功；本地后端 381、前端 67 通过 |

## 裁决记录

- 裁决：P0 E2E 在点击“批准并写入”后等待 apply 成功和按钮消失，再继续导航。
  理由：Linux trace 显示后续 `page.goto` 会取消尚未完成的 `POST /apply`，导致正式
  Knowledge 未落库；Windows 速度较快未稳定暴露。代价：E2E 辅助流程多一次有界等待，
  业务代码、截图断言、视口和阈值均不改变。
- 裁决：P3 E2E 等待 Source Draft 创建成功和 apply 完成后再导航。
  理由：Linux 全量 trace 显示 `POST /api/drafts` 被 `page.goto` 取消，属于同类测试
  竞态。代价：仅增加有界等待，不改变业务、断言口径或覆盖范围。
- 裁决：提交审核完成后不再重复点击队列项。
  理由：第二次队列点击会异步重新读取详情，慢 runner 下会覆盖刚编辑的表单。
  代价：删除冗余测试动作，不改变产品行为。
- 裁决：Migration checksum 先统一换行再计算 SHA256，并接受白名单中的旧 Windows
  checksum。理由：Git 索引使用 LF，Windows 工作区可能为混合换行；直接比较会使同一
  Migration 在两个平台不一致。代价：新库写入规范 checksum，已有本地 Vault 仍可打开。
