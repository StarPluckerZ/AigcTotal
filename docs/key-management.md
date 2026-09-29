# Key Management（签名密钥管理与泄露响应）

> 状态：生产签名形态已定（2026-09-24）——云上软件密钥 + 加密落盘 + 快速轮换，
> 安全性由透明日志架构（每日锚定 + keys.json git 信任根）保证，不依赖密钥不可导出。

## 1. 密钥分级的威胁模型

本系统**不是**靠"密钥偷不走"保证安全的（那是 HSM/KMS 的卖点，¥7000/月级别）。
透明日志的结构性优势在于：

1. **历史完整性不由密钥保证**：已发布的每日锚归档（GitHub Release）+ keys.json 的 git 历史
   就是"这些字节那天存在"的证据。在线密钥被偷也重写不了已发布的历史；
2. **伪造窗口被锚定频率硬性封顶**：用被偷密钥签出的假 checkpoint 链与昨日锚的树根冲突，
   `aigc-verify audit`（人、镜像、或 `verify-daily` 工作流）跑一次即暴露——
   发现延迟的上限就是锚定间隔；
3. **轮换不需要旧钥配合**：吊销/换 kid 是 keys.json 的一次 git 提交。密钥丢失
   （实例故障、误删）同样只是一个例行轮换事件。

因此密钥保管真正要买的是两样：**降低泄露概率**（下文加固）与**压缩泄露存续时间**
（下文 runbook + 每日检测）。

## 2. 密钥形态与保管规范

| 项 | 规范 |
|---|---|
| 算法/线格式 | ES256（ECDSA P-256 + SHA-256），签名 64 字节 P1363（契约见 `IReportSigner`） |
| 落盘形态 | **必须**加密 PKCS#8（`-----BEGIN ENCRYPTED PRIVATE KEY-----`，PBES2 / AES-256-CBC / PBKDF2-SHA-256 600k 迭代）——`aigc-log-host init/keys generate --pass-env` 产物 |
| 口令来源 | 只经环境变量（`--pass-env <VAR>`），不进盘、不进命令行参数、不进 git；生产用 systemd `EnvironmentFile=`（0600）或 `LoadCredential=` 注入 |
| 文件权限 | Unix 0600（工具自动设置）；Windows 放管理员私有的数据目录 |
| 备份纪律 | 加密钥文件可备份（口令另存）；**口令与钥文件不同存储位置**；明文钥绝不进快照/镜像/网盘 |
| 分工 | 在线钥（云宿主机，签 checkpoint/报告）与运维操作（keys.json 的 git 推送，GitHub 账号 + 2FA）分离；git 推送权限才是根权威 |
| 明文 PEM | 仅限本地开发；`init` 输出行会显式标注 `PLAINTEXT — dev only` |

## 3. 常规轮换 runbook（建议每季度或疑似暴露时）

```bash
export AIGC_KEY_PASS='<新口令>'
# 1) 生成新钥：keys.json 追加 active 记录（旧钥原样保留）
aigc-log-host keys generate --dir publish/public --key-out new-key.pem --pass-env AIGC_KEY_PASS
# 2) keys.json 提交推送——git 历史即锚定轮换时刻
git -C <public-repo> add well-known/keys.json && git -C <public-repo> commit -m "keys: rotate ..." && git push
# 3) 宿主机切换到新钥，下一轮 checkpoint 起用新 kid
systemctl restart aigc-log  # 或等效
# 4) 旧钥退役（verify_only：历史签名仍可验，带警告）
aigc-log-host keys retire --dir publish/public --kid k<旧64hex>
git commit + push
```

## 4. 泄露/丢失响应 runbook

**发现在线钥泄露（或疑似）**：

```bash
# 1) 立即吊销（不需要旧钥在场）
aigc-log-host keys revoke --dir publish/public --kid k<泄露64hex> \
  --reason "suspected compromise" --timestamp "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
# 2) 提交推送（吊销时刻被 git 锚定；此后用该钥签的任何东西按 KeyPolicy 一律拒绝）
git commit + push
# 3) 生成新钥并切换（见 runbook §3 步骤 1/3）
# 4) 复核泄露窗口：对当日已有锚跑全量审计，确认窗口内 checkpoint 未被伪造
aigc-verify audit --segments publish/public/segments --checkpoints publish/public/checkpoints \
  --keys publish/public/well-known/keys.json --anchor <最新tar>
# 5) 若发现伪造：公开披露（issue/Release note），受影响 checkpoint 之后的视图以锚为准
```

**密钥丢失（实例故障、误删，无私钥本体）**：同上，跳过第 1 步无法执行的部分——
直接 `keys generate` 新钥 + push + 旧钥标 `revoked`（时间填丢失判定时刻）。
丢失事件不破坏任何已发布历史。

## 5. 检测

- `verify-daily.yml`：每日从 Release 取最新锚归档，**仅凭归档本身**解包跑 `aigc-verify audit`
  （第三方视角，不信任仓内工作区）——篡改/伪造/密钥滥用分别倒在 manifest、树根、签名三道检查上；
- 任何第三方镜像可做同样的事（`MIRROR.md`）；外部见证越多，泄露被发现越快。

## 6. 硬件/TPM 路线（备注，当前不依赖）

vTPM（实例族支持时）或 YubiKey/Nitrokey HSM（离线根钥）可提供真不可导出性。
`IReportSigner` 契约已冻结（输入字节 → 64B P1363），PKCS#11 令牌（TPM/YubiKey/Nitrokey/
SoftHSM2 同一接口）可经一个 `Pkcs11Signer` 实现（host 侧，DER↔P1363 转换已备）
即插即用——在软件密钥方案稳定运行后作为增强项，不阻塞任何环节。
