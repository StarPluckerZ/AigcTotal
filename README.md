# AigcTotal

GB 45438-2025《网络安全技术 人工智能生成合成内容标识方法》隐式标识的确定性验证库——C#/.NET 生态的第一个实现。该强制性国标已于 2025-09-01 与《人工智能生成合成内容标识办法》同步施行。

> Deterministic verifier for AI-generated synthetic content labels per GB 45438-2025, the mandatory Chinese national standard in force since 2025-09-01.

## 它做什么

解析 AI 生成合成服务嵌入文件的隐式标识元数据（PNG / JPEG / MP4/MOV / WAV / MP3 / 文本），对照附录 E 七字段校验，产出四档判定：

| 判定 | 含义 |
|---|---|
| `compliant`（合规） | 有标识且符合附录 E |
| `noncompliant`（不合规） | 有标识但违反附录 E（字段缺失/非法/多标签冲突） |
| `not_found`（未检出） | 没有标识，也没有被抹除的痕迹 |
| `inconclusive`（无法判定） | 不能下结论：容器损坏、探测歧义、擦除证据、资源超限 |

完全确定性：同版本 + 同输入字节 → 逐字段一致的结果，可用于合规自检与可复现存证报告。

已对照真实世界样本验证：豆包（doubao）生成图片的 TC260 XMP 包装格式（`<TC260:AIGC>` 内嵌附录 E JSON）可正确解析。

## 当前状态

M1 进行中（2026-09）：

- [x] 不可信输入安全的有界解析（`BoundedReader`：总读取/单次分配/深度/结构数四类上限）
- [x] 载体探测（多探测器命中即报歧义，不静默）
- [x] PNG：`tEXt`（关键字 `AIGC`）与 `iTXt`（XMP，含 TC260 包装层）双通道；CRC 校验、截断取证；非元数据块（IDAT）流式跳过——多 MB 文件在极小读取预算内完成判定
- [x] JPEG：APP1 XMP 站点
- [x] MP4/MOV：box 树递归（`uuid` XMP + `udta/aigc` 双通道），`mdat` 零读取跳过，brand 细分记录
- [x] WAV：RIFF chunk 遍历（奇数长度填充处理）、`AIGC` chunk 站点、空壳取证
- [x] MP3：ID3v2.3/2.4 帧遍历、`TXXX`（描述=AIGC）站点、syncsafe 帧长
- [x] 文本：BOM/严格 UTF-8 探测、首尾提示语模式匹配（基线模式表）
- [x] 附录 E JSON 负载解码、逐站点 schema 校验、多站点聚合
- [x] 四档判定合成、同步 + 异步 API
- [x] `aigc-report` CLI：canonical 报告信封（schema v1）官方参考实现；JCS canonical JSON、ULID、CI 友好退出码
- [x] golden 语料库（`samples/corpus/`，确定性自造、无版权平台文件）+ golden 报告回归测试（schema 冻结执行器）
- [x] 健壮性套件进 CI：种子变异、截断、长度/深度炸弹——任何输入不允许异常逃出 `Verify`
- [ ] SharpFuzz 覆盖率引导模糊测试（`tools/AigcTotal.Fuzz`，Linux）——测试架已就绪
- [ ] XMP 命名空间精确匹配、MP4 `udta` atom 名、文本提示语模式表——待 TC260 指南原文校准

## 使用

### CLI（`aigc-report`）——校验报告官方参考实现

```console
$ aigc-report image.png video.mp4          # 在输入旁生成 <file>.report.json
$ aigc-report --stdout article.txt         # 单文件 canonical JSON 输出到 stdout
$ aigc-report --pretty image.png           # 人类可读（非 canonical）
```

默认输出是 **canonical JSON（RFC 8785 受限子集）**——被哈希、签名、入透明日志的正是这串字节。退出码可直接用于发布前 CI 门禁：`0` 合规 / `1` 不合规 / `2` 未检出 / `3` 无法判定 / `10` 处理错误（多文件取最严重）。

### 库

```csharp
using AigcTotal.GB45438;

using var stream = File.OpenRead("aigc-image.png");
VerificationResult result = AigcLabelVerifier.Verify(stream);

Console.WriteLine(result.Verdict);          // Compliant / Noncompliant / NotFound / Inconclusive
foreach (var site in result.Sites)
{
    Console.WriteLine($"{site.Encoding} at {site.Location.Offset}: {site.Fields?["ContentProducer"]}");
}
```

## 设计原则

- **零第三方依赖**（`net10.0`；`netstandard2.0` 仅引 `System.Memory`）。
- **不可信输入安全**：所有读取经有界读取器执行四类上限；实际使用的限制值随结果回显（可复现性的一部分）。
- **证据与结论两分**：`sites`/`signals` 是物理事实，`checks` 是规则判定，`verdict` 由冻结规则合成——从不猜测。
- **稳定词表**：check id、判定值、字段名是冻结令牌（见 `docs/checks.md`）；面向用户的中文文案属于展示层，不属于本库。
- **防假性判定**：通道未实现/无法解码 → `inconclusive`，绝不假装 `not_found`。

## 词表与 schema

- `docs/checks.md` — check id 注册表（只增不改）
- `docs/canonicalization.md` — 存证报告 canonical JSON 规范
- `docs/fuzzing.md` — 模糊测试运行指南

## 许可证

[Apache-2.0](LICENSE)
