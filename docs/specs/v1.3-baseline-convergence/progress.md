# 实施进度台账

> 需求名：v1.3-baseline-convergence ｜ 更新：2026-11-06

| 任务 | 状态 | 验证证据 / 裁决 |
|---|---|---|
| T1 扩展 AI 工作区历史筛选 | 完成 | `npm test -- draft-review-editing` 14/14；`npm run typecheck`、`npm run lint`、`npm run format:check` 通过 |
| T2 增加 Linux 截图基准 | 完成（本地） | Ubuntu 24.04 WSL / Linux Chromium P0 4/4、全量 E2E 14/14；`knowledge-library-linux.png` 1280×720，SHA256 `1599CB0DF461A7A6B9D3C0335A701458D80BD4F6E042CD47025642944E50FD49`；Windows SHA256 未变；GitHub CI 待提交后验证 |
| T3 收敛产品与验收文档 | 完成 | 产品设计已移除固定成熟度默认表、补齐三类只读整理规则与四种历史终态；P0 progress 增加 T77；设计旧架构路径依赖已移除 |
| T4 文档 Git 白名单落地 | 完成 | `git diff --cached --name-only` 仅包含指定产品设计、P0 证据、convergence 文档与本次前端源码/测试；未暂存其他 `docs/` 目录 |
| T5 非回归与收敛验证 | 完成（本地） | 后端 381、前端 67、Windows E2E 14、Linux E2E 14、build/format/typecheck/lint 通过；AC28/29/36 的 GitHub CI 证据待提交后补齐 |

## 裁决记录

- 裁决：P0 E2E 在点击“批准并写入”后等待 apply 成功和按钮消失，再继续导航。
  理由：Linux trace 显示后续 `page.goto` 会取消尚未完成的 `POST /apply`，导致正式
  Knowledge 未落库；Windows 速度较快未稳定暴露。代价：E2E 辅助流程多一次有界等待，
  业务代码、截图断言、视口和阈值均不改变。
- 裁决：P3 E2E 等待 Source Draft 创建成功和 apply 完成后再导航。
  理由：Linux 全量 trace 显示 `POST /api/drafts` 被 `page.goto` 取消，属于同类测试
  竞态。代价：仅增加有界等待，不改变业务、断言口径或覆盖范围。
