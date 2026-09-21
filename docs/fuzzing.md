# Fuzzing（模糊测试）

解析器吃的是不可信输入。两层防线：

1. **`tests/.../RobustnessTests.cs`**——确定性变异 + 退化输入（长度炸弹、深度炸弹、截断、随机位翻转），全平台 CI 每次跑；
2. **`tools/AigcTotal.Fuzz`**——SharpFuzz/libFuzzer 覆盖率引导的正式模糊测试，Linux/macOS 本地或定期跑。

## 契约

对**任何**字节序列：

- `AigcLabelVerifier.Verify` 不允许有异常逃出（结构/资源异常必须消化为诊断 + `inconclusive`）；
- `verdict` 必须落在四档枚举内；
- `checks` 列表非空（探测层检查始终存在）。

## 运行（Linux）

```console
# 构建（libFuzzer 模式需要插桩环境 DOTNET_SHARPFUZZ_INSTRUMENT）
cd tools/AigcTotal.Fuzz
dotnet build -c Release

# 以 libFuzzer 驱动运行（需 dotnet-sharpfuzz 环境准备，见 SharpFuzz 文档）
# https://github.com/SharpFuzz/SharpFuzz
sharpfuzz AigcTotal.GB45438.dll
dotnet bin/Release/net8.0/AigcTotal.Fuzz.dll corpus_out samples/corpus
```

- 种子语料：`samples/corpus/`（aigc-fixtures 生成，13 个覆盖六类载体的自造文件）；
- 崩溃输入 = SharpFuzz 报告的 `crash-<hash>` 文件，修复时应转化为 `RobustnessTests` 的回归用例。

## 修复流程

发现崩溃 → 最小化 → 在 `RobustnessTests` 加回归用例 → 修复 → 全绿 → 把该输入追加进种子语料。
