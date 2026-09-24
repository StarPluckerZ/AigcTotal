# 镜像纪律（MIRROR.md）

透明日志公开物（`segments/` + `checkpoints/` + `well-known/keys.json` + 每日 tarball）的镜像约定。

## 原则

1. **公开物可完整镜像**：透明日志的全部信任材料是静态文件，任何第三方可以、也被鼓励
   全量镜像——不需要许可，不需要协调。
2. **镜像不是信任源**：信任源 = 公开物本身 + 开源验证工具（`aigc-verify`）。镜像只是
   可得性冗余；验证者应对照多个独立镜像（或自持副本）确认字节一致。
3. **一致性锚**：每日 tarball（GitHub Releases 日期 tag）与 git 历史是外部见证。镜像之间
   字节不一致时，以能通过 manifest SHA-256 整验 + 外部锚见证链回溯的一方为准。
4. **被攻击时审计可得性不受影响**：主发布点（GitHub）若不可用或被篡改，任何持有历史
   tarball/克隆仓库的第三方仍可完成全部验证——这正是外部锚设计的抗胁迫目标。

## 镜像操作

- 每日 tarball 文件名即日期（`yyyy-MM-dd.tar`），内容确定性（同目录重打包逐字节一致），
  镜像方按文件名+SHA-256 直接校对；
- 增量镜像只需同步新增的段文件与 checkpoint（旧文件 append-only 永不变化）；
- 镜像方不应（也无法）重写历史：任何对既有文件的改动都会导致 manifest/树根/链校验失败，
  在 `aigc-verify audit` 下立即暴露。

## 建议镜像点（按需开设）

- Git 仓库镜像（含 well-known/ 与历史 manifest）；
- 对象存储/静态站（tarball 与 segments/ 全量）；
- 验证者自持副本：定期 `aigc-verify audit` + 归档当日 tarball 即构成个人级见证。
