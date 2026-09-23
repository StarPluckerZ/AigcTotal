# 安全政策（Security Policy）

> English summary below.

## 支持版本

| 版本 | 支持状态 |
|---|---|
| 0.1.x | ✅ 当前开发线 |
| 更早版本 | ❌ 请升级 |

## 报告漏洞

本项目解析**不可信输入**（任意字节序列的容器文件），解析器的内存与资源安全是核心安全主张。如发现以下类别的问题，请通过 **GitHub 私有漏洞报告**（Security → Report a vulnerability）私下报告：

- `AigcLabelVerifier.Verify` 对任何输入抛出未消化异常（契约：任何输入不逃异常、verdict 落在四档内、checks 非空）；
- 内存破坏（越界读写、无限分配、栈耗尽）；
- 资源耗尽（读取/分配/深度/结构数四类上限可被绕过）；
- 判定完整性问题（同一版本同一输入产出不一致结果；报告字节形态与 schema 不符）。

**请勿**为演示漏洞而公开提交可触发的恶意样本文件到 issue。

报告内容请附：复现输入（最小化后）、影响描述、`aigc-report --version`。

## 处理流程与时间线

1. **48 小时内**确认收到；
2. **7 日内**给出初步评估（是否成立、严重级别）；
3. 修复随下一个补丁版本发布；发布前向报告者同步修复内容并致谢（可匿名）；
4. 高危（内存破坏级）立即修复并发布补丁版本 + 公开致谢报告者（见开发计划的风险承诺）。

## 致谢

修复的安全问题会在 Release Notes 中公开致谢报告者（除非要求匿名）。

---

**English summary**: AigcTotal parses untrusted binary input. Core guarantees: no exception ever escapes `AigcLabelVerifier.Verify`, verdicts stay within the four-value enum, and the four resource limits (total read / allocation / depth / structures) hold for any input. Please report violations privately via GitHub's private vulnerability reporting. We acknowledge reporters in release notes (anonymous on request).
