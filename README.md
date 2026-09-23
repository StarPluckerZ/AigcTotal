# AigcTotal

GB 45438-2025《网络安全技术 人工智能生成合成内容标识方法》隐式标识的确定性验证库——C#/.NET 生态的第一个实现。该强制性国标已于 2025-09-01 与《人工智能生成合成内容标识办法》同步施行。

> Deterministic verifier for AI-generated synthetic content labels per GB 45438-2025, the mandatory Chinese national standard in force since 2025-09-01.

## 它做什么

解析 AI 生成合成服务嵌入文件的隐式标识元数据，对照附录 E 七字段校验，产出四档判定。载体覆盖 14 类（含对应显式/文档通道）：

| 类别 | 载体与通道 |
|---|---|
| 图片 | PNG（tEXt / zTXt / iTXt-XMP）、JPEG（APP1-XMP / EXIF UserComment）、WebP、TIFF、GIF |
| 视频 | MP4/MOV/M4A（uuid-XMP、udta keys/ilst）、AVI（LIST/INFO） |
| 音频 | WAV（AIGC chunk）、MP3（ID3v2.3/2.4 TXXX）、FLAC/OGG（Vorbis Comment） |
| 文档 | OOXML 文档族（docProps/custom.xml）、PDF（Info Dict /AIGC）、Markdown front matter |
| 纯文本 | 首尾显式提示语（要素组合匹配） |

| 判定 | 含义 |
|---|---|
| `compliant`（合规） | 有标识且符合附录 E |
| `noncompliant`（不合规） | 有标识但违反附录 E（字段缺失/非法/多标签冲突） |
| `not_found`（未检出） | 没有标识，也没有被抹除的痕迹 |
| `inconclusive`（无法判定） | 不能下结论：容器损坏、探测歧义、擦除证据、资源超限 |

完全确定性：同版本 + 同输入字节 → 逐字段一致的结果，可用于合规自检与可复现存证报告。

已对照真实世界样本验证：豆包（doubao）生成图片的 TC260 XMP 包装格式（`<TC260:AIGC>` 内嵌附录 E JSON）可正确解析。

## 当前状态

M1 完成（2026-09）。判定核心、报告信封与语料/回归体系均已落地：

- [x] 不可信输入安全的有界解析（`BoundedReader`：总读取/单次分配/深度/结构数四类上限；流式扫描原语供零保留校验/跳读）
- [x] 载体探测（多探测器命中即报歧义，不静默）
- [x] PNG：`tEXt`（关键字 `AIGC`）、`zTXt`（zlib 解压，膨胀封顶）、`iTXt`（XMP，含 TC260 包装层）三通道；CRC 校验、截断取证；非元数据块（IDAT）流式跳过
- [x] JPEG：APP1 XMP 与 EXIF UserComment（TC260-PG-20259A 附录 B 包裹形态）双通道；普通备注文本静默忽略，不误判
- [x] MP4/MOV/M4A：box 树递归（`uuid` XMP + `udta` keys/ilst 双通道），`mdat` 零读取跳过，brand 细分记录（QuickTime/M4A MIME 正确映射）
- [x] WAV：RIFF chunk 遍历（奇数长度填充处理）、`AIGC` chunk 站点、空壳取证
- [x] MP3：ID3v2.3/2.4 帧遍历（v2.4 扩展头 syncsafe）、`TXXX`（描述=AIGC）站点；v2.2 显式报不支持（不出假 not_found）
- [x] 文本：BOM/严格 UTF-8（全量流式校验，大文件只取首尾窗口判定）、首尾提示语要素组合匹配、Markdown front matter
- [x] 载体横向扩张（共 14 类）：FLAC/OGG、AVI、WebP、TIFF、GIF、OOXML 文档族、PDF（头/尾窗口有界裸扫描）
- [x] 附录 E JSON 负载解码（严格：重复键拒绝、`\u` 严格十六进制）、逐站点 schema 校验、多站点聚合、四档判定、同步 + 异步 API
- [x] `aigc-report` CLI：canonical 报告信封（schema v1）官方参考实现；JCS canonical JSON（含 RFC 8785 孤立代理项转义）、ULID、CI 友好退出码
- [x] golden 语料库（`samples/corpus/`，确定性自造、无版权平台文件）+ **全量 golden 报告回归**（45 份，四档判定 × 载体/负载形态/失败码/取证信号矩阵，`aigc-fixtures golden` 生成、逐字节比对、覆盖面地板断言）
- [x] 健壮性套件进 CI：种子变异、截断、长度/深度炸弹——任何输入不允许异常逃出 `Verify`
- [x] **TC260 四份载体指南 + GB 45438-2025 正文双重校准完成**（2026-09-22）：MP4 keys/ilst 通道（PG-20257A）、PNG tEXt 包裹形态（PG-20259A 附录B）、XMP 官方命名空间、附录 E j) 字符集白名单、6.1 c) 唯一标识（重复打标=不合规）、5.1 文本显式标识要素组合匹配
- [x] netstandard2.0 行为冒烟套件（net10.0 运行器引用 ns2.0 编译产物，免装 .NET Framework 开发包）
- [x] **`AigcTotal.Log` 透明日志库**（2026-09-23，TDD）：RFC 6962 k-split Merkle（官方向量 + 3.6M 叶真实包含证明进 CI）、append-only 段文件（崩溃残行容忍）、checkpoint 链（prev-hash 自哈希、canonical 字面量钉死）、keys.json 状态机（吊销半开区间语义）、ES256 线格式（SPKI/kid/DER↔P1363）；`aigc-verify` CLI 与签名服务随后续步骤落地
- [ ] 完整 RFC 8785 附录测试向量集（当前为附录 B/D 向量子集）
- [x] SharpFuzz 覆盖率引导模糊测试（`tools/AigcTotal.Fuzz`，Linux）——在 Debian 小主机夜间运行（未进 GitHub CI）
- [ ] 缓议载体：HEIF/HEIC、FLV、MKV/WebM（EBML）、OFD/xmind/UOF

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
