# Fuzzing（模糊测试）

解析器吃的是不可信输入。两层防线：

1. **`tests/.../RobustnessTests.cs`**——确定性变异 + 退化输入（长度炸弹、深度炸弹、截断、随机位翻转），全平台 CI 每次跑；
2. **`tools/AigcTotal.Fuzz`**——SharpFuzz + libfuzzer-dotnet 覆盖率引导的正式模糊测试，在 Linux 宿主（如 Debian 小主机）上运行。

## 契约

对**任何**字节序列：

- `AigcLabelVerifier.Verify` 不允许有异常逃出（结构/资源异常必须消化为诊断 + `inconclusive`）；
- `verdict` 必须落在四档枚举内；
- `checks` 列表非空（探测层检查始终存在）。

任何违例即 crash：libFuzzer 保存 `crash-<hash>` 输入文件。

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

# 跑：种子 = 仓库 samples/corpus（六类载体自造语料）
./libfuzzer-dotnet-debian AigcTotal.Fuzz.dll SharpFuzz.Common.dll -- \
  -max_len=262144 -rss_limit_mb=2048 -max_total_time=3600 \
  ./corpus ../../../../../../samples/corpus
```

- `-workers=N -jobs=N` 可并行（小主机 4 核建议 3，留一核给系统）；
- 发现 crash → 输入已存为 `crash-<hash>`：最小化后在 `RobustnessTests` 加回归用例，修复，并把该输入追加进 `samples/corpus/`（经 aigc-fixtures 或直接放入）；
- 长期挂机用 systemd timer（见部署机 `/opt/aigc-fuzz/run-nightly.sh`）。
