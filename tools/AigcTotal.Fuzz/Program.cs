using System;
using System.IO;
using System.Text;
using AigcTotal.GB45438;
using AigcTotal.Log.Anchoring;
using AigcTotal.Log.Checkpoints;
using AigcTotal.Log.Keys;
using AigcTotal.Log.Proofs;
using AigcTotal.Log.Signing;
using AigcTotal.Report;

namespace AigcTotal.Fuzz
{
    /// <summary>
    /// 覆盖率引导模糊测试目标（SharpFuzz + libfuzzer-dotnet 驱动，Linux 运行，见 docs/fuzzing.md）。
    /// 目标经环境变量 AIGC_FUZZ_TARGET 选择（缺省 gb45438）：
    ///   gb45438        Verify 对任何字节序列都不抛异常——结构/资源异常必须消化为诊断与 inconclusive；
    ///                  verdict 落在四档枚举内。任何异常逃出即 bug。
    ///   canonical-json CanonicalJson.Deserialize——只允许 FormatException 逃逸（文档化的格式拒绝）。
    ///   signed-report  SignedReportCodec.Parse——FormatException/ArgumentException 之外即 bug。
    ///   proof          ProofCodec.Parse——同上。
    ///   keys           KeyStore.Parse——同上。
    ///   checkpoint     CheckpointCodec.Parse——同上。
    ///   tar            DeterministicTar.Read（锚归档，从 Release 下载的不可信二进制）——只允许 FormatException。
    ///   anchor         LocalAnchorProvider.ReadArchive（tar + manifest + checkpoint 组合面）——只允许 FormatException。
    /// 逃逸契约之外的任何异常（OverflowException/IndexOutOfRange/OutOfMemory/…）= bug，libFuzzer 记 crash。
    /// </summary>
    public static class Program
    {
        private static readonly string TempInput =
            Path.Combine(Path.GetTempPath(), "aigc-fuzz-input.bin");

        public static void Main(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);
            string target = Environment.GetEnvironmentVariable("AIGC_FUZZ_TARGET") ?? "gb45438";
            SharpFuzz.Fuzzer.LibFuzzer.Run((ReadOnlySpan<byte> data) =>
            {
                byte[] bytes = data.ToArray();
                switch (target)
                {
                    case "gb45438":
                        VerificationResult result = AigcLabelVerifier.Verify(bytes);
                        if ((int)result.Verdict is < 1 or > 4)
                        {
                            throw new InvalidOperationException($"verdict out of range: {result.Verdict}");
                        }
                        break;
                    case "canonical-json":
                        OnlyFormat(() => CanonicalJson.Deserialize(Encoding.UTF8.GetString(bytes)));
                        break;
                    case "signed-report":
                        FormatOrArgument(() => SignedReportCodec.Parse(Encoding.UTF8.GetString(bytes)));
                        break;
                    case "proof":
                        FormatOrArgument(() => ProofCodec.Parse(Encoding.UTF8.GetString(bytes)));
                        break;
                    case "keys":
                        FormatOrArgument(() => KeyStore.Parse(Encoding.UTF8.GetString(bytes)));
                        break;
                    case "checkpoint":
                        FormatOrArgument(() => CheckpointCodec.Parse(Encoding.UTF8.GetString(bytes)));
                        break;
                    case "tar":
                        OnlyFormat(() => DeterministicTar.Read(WriteTemp(bytes)));
                        break;
                    case "anchor":
                        OnlyFormat(() => LocalAnchorProvider.ReadArchive(WriteTemp(bytes)));
                        break;
                    default:
                        throw new InvalidOperationException($"unknown AIGC_FUZZ_TARGET '{target}'");
                }
            });
        }

        private static string WriteTemp(byte[] bytes)
        {
            File.WriteAllBytes(TempInput, bytes);
            return TempInput;
        }

        private static void OnlyFormat(Action parse)
        {
            try
            {
                parse();
            }
            catch (FormatException)
            {
                // 文档化的格式拒绝
            }
        }

        private static void FormatOrArgument(Action parse)
        {
            try
            {
                parse();
            }
            catch (FormatException)
            {
                // 文档化的格式拒绝
            }
            catch (ArgumentException)
            {
                // 文档化的形态拒绝（kid/签名值/字段形态）
            }
        }
    }
}
