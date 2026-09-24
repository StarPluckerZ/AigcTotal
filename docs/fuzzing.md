# Fuzzing（模糊测试）

解析器吃的是不可信输入。两层防线：

1. **`tests/.../RobustnessTests.cs`**——确定性变异 + 退化输入（长度炸弹、深度炸弹、截断、随机位翻转），全平台 CI 每次跑；
2. **`tools/AigcTotal.Fuzz`**——SharpFuzz + libfuzzer-dotnet 覆盖率引导的正式模糊测试，在 Linux 宿主（如 Debian 小主机）上运行。

## 契约

目标经环境变量 `AIGC_FUZZ_TARGET` 选择（见 `tools/AigcTotal.Fuzz/Program.cs`）。对**任何**字节序列：

- `gb45438`：`AigcLabelVerifier.Verify` 不允许有异常逃出（结构/资源异常必须消化为诊断 + `inconclusive`）；
  `verdict` 必须落在四档枚举内；`checks` 列表非空（探测层检查始终存在）；
- `canonical-json` / `tar` / `anchor`：只允许 `FormatException` 逃逸（文档化的格式拒绝）；
- `signed-report` / `proof` / `keys` / `checkpoint`：只允许 `FormatException`/`ArgumentException` 逃逸
  （格式与形态拒绝）；
- `anchor` 额外保证：manifest 声明的文件集与归档实际内容不一致（缺文件/多文件）即拒绝。

任何契约外异常（`OverflowException`、索引越界、`KeyNotFoundException`、OOM……）即 bug：
libFuzzer 保存 `crash-<hash>` 输入文件。2026-09-24 一轮跨平台变异回归（`LogRobustnessTests`）
即抓出三个"字段数正确但键名错 → KeyNotFoundException 逃逸"（SignedReport/Proof/Checkpoint codec）
与两个 tar 八进制/size 溢出缺陷——历史教训，契约外异常类一律按此处理。

## 运行（Linux 宿主一次性配置）

```console
# 1) .NET 10 SDK
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0
export PATH="$HOME/.dotnet:$PATH"

# 2) 插桩工具
dotnet tool install --global SharpFuzz.CommandLine

# 3) libfuzzer-dotnet 原生驱动（预编译）
curl -sLO https://github.com/Metalnem/libfuzzer-dotnet/releases/latest/download/libfuzzer-dotnet-debian
chmod +x libfuzzer-dotnet-debian
```

## 每轮运行

```console
cd tools/AigcTotal.Fuzz
dotnet build -c Release
cd bin/Release/net10.0

# 插桩被测程序集（每次重新构建后都要重跑）
sharpfuzz AigcTotal.GB45438.dll
sharpfuzz AigcTotal.Log.dll

# 跑：种子 = 仓库 samples/corpus（六类载体自造语料）
./libfuzzer-dotnet-debian AigcTotal.Fuzz.dll SharpFuzz.Common.dll -- \
  -max_len=262144 -rss_limit_mb=2048 -max_total_time=3600 \
  ./corpus ../../../../../../samples/corpus

# 透明日志各目标：种子 = tools/AigcTotal.Fuzz/seeds/<target>/
for t in canonical-json signed-report proof keys checkpoint tar anchor; do
  AIGC_FUZZ_TARGET=$t ./libfuzzer-dotnet-debian AigcTotal.Fuzz.dll SharpFuzz.Common.dll --     -max_len=65536 -rss_limit_mb=2048 -max_total_time=600     "seeds/$t"
done
```

- `-workers=N -jobs=N` 可并行（小主机 4 核建议 3，留一核给系统）；
- `tar`/`anchor` 目标的语料来自外部下载（GitHub Releases），与线上暴露面一致，优先保证时长；
- 发现 crash → 输入已存为 `crash-<hash>`：最小化后在 `RobustnessTests` 加回归用例，修复，并把该输入追加进 `samples/corpus/`（经 aigc-fixtures 或直接放入）；
- 长期挂机用 systemd timer（见部署机 `/opt/aigc-fuzz/run-nightly.sh`）。
