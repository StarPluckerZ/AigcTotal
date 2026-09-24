# RFC 8785（JCS）一致性证明报告

- 生成时间：2026-09-23 04:13:47 UTC
- 被测实现：AigcTotal.Rfc8785 v0.1.0（net10.0，运行于 10.0.10）
- 验证基线：RFC 8785 正文 §3.2.2–§3.2.4 与附录 B/C/D/E 全部向量；附录 I 开发门户官方测试数据（cyberphone/json-canonicalization testdata）；
- 独立预言机：Node/V8（附录 A 示例规范化器逐字实现 + JSON.stringify 的 ECMA-262 Number::toString）（v26.4.0）

## 摘要

| # | 验证组 | 检查数 | 通过 | 失败 | 结论 |
|---|--------|--------|------|------|------|
| 1 | RFC 8785 §3.2.2/§3.2.4 原语序列化与 UTF-8 生成 | 2 | 2 | 0 | PASS |
| 2 | RFC 8785 §3.2.3 UTF-16 码元序排序 | 2 | 2 | 0 | PASS |
| 3 | RFC 8785 附录 B 表 1（Number Serialization Samples） | 26 | 26 | 0 | PASS |
| 4 | RFC 8785 附录 C/D/E 正文样例 | 5 | 5 | 0 | PASS |
| 5 | 开发门户官方测试数据（cyberphone/json-canonicalization testdata，Apache-2.0） | 6 | 6 | 0 | PASS |
| 6 | Node/V8 附录 A 逐字实现差分（结构语料） | 2505 | 2505 | 0 | PASS |
| 7 | V8 数字大规模差分（JSON.stringify ≡ ECMA-262 Number::toString） | 199999 | 199999 | 0 | PASS |

**合计：202545 项检查，202545 通过，0 失败**（0 组环境性跳过）。

## 证明结构

1. **规范向量**：RFC 8785 全部规范性测试数据——§3.2.2 原语序列化、§3.2.3 UTF-16 码元序排序、
   §3.2.4 逐字节 UTF-8 输出、附录 B 表 1 全部 26 行（含 NaN/Infinity 的 MUST 终止行）。
2. **附录样例**：附录 C 键序、附录 D 大数终止/字符串包装、附录 E 子类型透传。
3. **官方扩展向量**：附录 I 开发门户 testdata（values/weird/french/structures/arrays/unicode 六组）逐字节比对。
4. **引擎差分**：附录 A 的 ECMAScript 示例规范化器逐字运行于 V8，对程序化结构语料全量对账；
   数字侧以 V8 JSON.stringify（ECMA-262 §7.1.12.1 的规范实现）作 20 万级样本差分。
5. **不变量**：全部数字输出做「解析回原 double 位形」的往返校验（dotnet test 内常态化执行）。

## 各组明细

### 1. RFC 8785 §3.2.2/§3.2.4 原语序列化与 UTF-8 生成

- 结论：PASS（2/2）；canonical + UTF-8 字节向量一致
- §3.2.2 规范形态逐字一致
- §3.2.4 UTF-8 字节级向量：一致（118 字节）

### 2. RFC 8785 §3.2.3 UTF-16 码元序排序

- 结论：PASS（2/2）；一致
- 规范形态一致（U+0080 依 §3.2.2.2 原样输出）
- 「Expected argument order after sorting」逐项一致（UTF-16 码元序）

### 3. RFC 8785 附录 B 表 1（Number Serialization Samples）

- 结论：PASS（26/26）；全部一致
- 含 2 的幂边界（~2**68、1e+23 邻域）、±0、次正规极值与 Note 2 平局取偶（Round to even）行

### 4. RFC 8785 附录 C/D/E 正文样例

- 结论：PASS（5/5）；全部一致
- 附录 C 地址记录按 address/city/name/state/zip 输出：一致
- 附录 D 1.4e+9999 超出 binary64 → MUST 终止（推荐字符串包装的原因）：一致
- 附录 D 字符串包装的巨数原样透传：一致
- 附录 D int64Max 按 binary64 语义变形（V8 同为 9223372036854776000）：一致
- 附录 E 纯字符串子类型透传（"big":"055" 保持原样）：一致

### 5. 开发门户官方测试数据（cyberphone/json-canonicalization testdata，Apache-2.0）

- 结论：PASS（6/6）；全部逐字节一致
- arrays.json：逐字节一致
- french.json：逐字节一致
- structures.json：逐字节一致
- unicode.json：逐字节一致
- values.json：逐字节一致
- weird.json：逐字节一致

### 6. Node/V8 附录 A 逐字实现差分（结构语料）

- 结论：PASS（2505/2505）；V8 全量一致（node v26.4.0）
- 附录 A 代码逐字拷贝自 RFC 8785（V8 = JSON.stringify/JSON.parse 规范引擎），node v26.4.0
- 语料含 RFC/官方样例原文 + 2500 条程序化生成（含控制字符、C1、 emoji、非规范化 Unicode、乱序键）

### 7. V8 数字大规模差分（JSON.stringify ≡ ECMA-262 Number::toString）

- 结论：PASS（199999/199999）；V8 全量一致
- 样本 = 全随机位型 / 正规 binade 聚焦 / 次正规 / 10 的幂 ±1ulp / 2 的幂 ±1ulp / 小数邻域（node v26.4.0）
- 跳过非有限样本 1 个（MUST 终止行为见附录 B 组）

