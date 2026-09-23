# 贡献指南（Contributing）

感谢关注 AigcTotal。这是一个确定性验证库：**可复现性是产品主张**，因此贡献规则比一般开源项目严格。

## 环境要求

- .NET 10 SDK（`dotnet --version` ≥ 10.0）
- 无其他依赖：库目标 `net10.0` 零第三方包，`netstandard2.0` 仅 `System.Memory`

## 构建与测试

```console
dotnet build -c Release          # 必须零警告（TreatWarningsAsErrors 常开）
dotnet test -c Release           # 全部测试（含 netstandard2.0 冒烟套件）
```

netstandard2.0 行为由独立冒烟工程覆盖（`tests/AigcTotal.GB45438.NetStandard.Tests`，net10.0 运行器引用 ns2.0 编译产物），无需安装 .NET Framework 开发包；也可在 Linux SDK 容器中运行：

```console
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test -c Release
```

## 硬性规则

1. **零警告门禁**：PR 引入任何编译警告即拒绝。
2. **词表只增不改**：check id、code、carrier 令牌、枚举值是冻结词表（见 `docs/checks.md`）。新增允许，重命名/删除/改变语义需先开 issue 讨论。
3. **golden 回归**：`samples/corpus/` 由 `aigc-fixtures` 确定性生成。改动了语料或判定语义后：
   - 重新生成语料并确认 manifest 一致；
   - golden 报告（`tests/AigcTotal.Report.Tests/Golden/`）是 schema 冻结的物理执行器——**逐字节 diff 的任何变化都必须是有意识的、在 PR 描述中说明理由的**。
4. **不可信输入契约**：`Verify` 对任何字节序列不得逃出异常，verdict 必须落在四档内。新解析器必须配齐"炸弹"回归用例（长度/深度/截断/EOF），参考 `RobustnessTests`。
5. **测试必须随代码**：新载体/新通道 = happy path + 未检出 + 畸形降档三件套。

## 模糊测试

`tools/AigcTotal.Fuzz`（SharpFuzz + libfuzzer-dotnet，Linux）为正式模糊测试入口，运行方式见 `docs/fuzzing.md`。崩溃输入的处理流程：最小化 → 加进 `RobustnessTests` 回归 → 修复 → 原始输入追加为 fixtures 种子（`aigc-fixtures` 必须能确定性重放它）。

## 新增载体的流程

1. 开 issue：给出 TC260 指南或国标条款依据、负载存储位置、parse 通道；
2. 实现 `ICarrierParser`（只经 `BoundedReader` 读取；探测歧义/资源超限 → 降档不猜测）；
3. 注册探测器、解析器、check id（只增）、`docs/checks.md` 行、语料构造器与 fixtures 条目；
4. 端到端测试 + 炸弹测试齐备后合入。

## 提交信息

采用约定式前缀：`feat:` / `fix:` / `fuzz:` / `docs:` / `test:`，范围可带载体名（如 `fix(mp3):`）。

## 许可

贡献即同意以 [Apache-2.0](LICENSE) 授权。
