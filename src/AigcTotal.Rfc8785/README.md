# AigcTotal.Rfc8785

RFC 8785「JSON Canonicalization Scheme (JCS)」完整实现：严格 I-JSON 解析、
ECMAScript 原语序列化（§3.2.2）、UTF-16 码元序属性排序（§3.2.3）、无空白 UTF-8 输出（§3.2.4）。
双目标 `net10.0;netstandard2.0`，零第三方依赖。

## API

```csharp
JsonCanonicalizer.Canonicalize(string json)            // JSON 文本 → 规范化文本
JsonCanonicalizer.CanonicalizeToUtf8(string json)      // → 规范化 UTF-8 字节（§3.2.4，可哈希/签名）
JsonCanonicalizer.CanonicalizeToUtf8(byte[] utf8Json)  // 严格 UTF-8 解码（非法字节即报错，不做 U+FFFD 替换）
JsonCanonicalizer.Serialize(object? dom)               // 程序化 DOM → 规范化文本
```

DOM 值域：`null / bool / double / long / int / byte / sbyte / short / ushort / uint /
string / List<object?> / Dictionary<string, object?>`。整型按 ECMAScript Number 语义转
binary64（≥ 2^53 丢精度，与 JS 一致）；`float / decimal / ulong` 无 ECMAScript 对应物，明确拒绝。

## 关键实现决策

- **数字序列化不依赖运行时格式化**：内部 `Es6NumberSerializer` 用 BigInteger 精确算术
  （Dragon4 风格舍入区间法）实现 ECMA-262 §7.1.12.1（含 Note 2 就近舍入/平局取偶）。
  2 的幂边界处舍入区间不对称（下方间隙减半），且「正确舍入的 p 位十进制」未必可往返，
  候选集须含 floor/±1 变体。实测 .NET 10 的 `ToString("R")` 在此类边界会给出**不满足往返**
  的输出（如 bits `0410000000000000` 的 16 位结果，V8 与精确区间法均给 17 位），故不可用作捷径。
- **拒绝即错误**：NaN/Infinity、超出 binary64 的数字（如 `1e999`）、孤立代理项、重复属性名、
  非法 JSON 语法均抛 `JsonCanonicalizationException`（RFC 8785 §2/§3.1 与 I-JSON 的 MUST 义务）。
- **排序**：`StringComparer.Ordinal` == UTF-16 码元逐位比较，即 §3.2.3 要求。
- **字符串**：U+0000–U+001F 用 `\b \t \n \f \r` 或小写 `\uhhhh`；`"` 与 `\` 转义；其余（含
  U+0080、非规范化 Unicode、emoji）原样输出；孤立代理项 MUST 终止。

## 一致性证明

`tools/AigcTotal.Rfc8785.Conformance` 汇总七组验证（正文向量、附录 B 26 行、附录 C/D/E、
附录 I 官方 testdata 逐字节、V8 附录 A 逐字实现差分、V8 数字 20 万样本差分），
报告见 `docs/rfc8785-conformance.md`。重新生成：

```bash
dotnet run --project tools/AigcTotal.Rfc8785.Conformance -- --out docs/rfc8785-conformance.md
```

官方测试数据快照与出处见 `standards/rfc8785/`（含 RFC 原文与 Apache-2.0 testdata）。

## 与 AigcTotal.Report CanonicalJson 的关系

`AigcTotal.Report.CanonicalJson` 是 JCS 的**受限子集**（信封 schema 禁浮点，仅整数，
孤立代理项转义而非拒绝），服务于报告信封的确定性序列化。本库是**完整 RFC 8785**实现
（全量数字域 + 严格 I-JSON 拒绝语义），供需要规范级可互操作哈希/签名的场景使用。
两者的键排序、字符串转义、无空白 UTF-8 输出在共同子集上逐字节一致。
