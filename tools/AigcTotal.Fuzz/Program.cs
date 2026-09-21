using System;
using System.IO;
using AigcTotal.GB45438;

namespace AigcTotal.Fuzz
{
    /// <summary>
    /// 覆盖率引导模糊测试目标（SharpFuzz/libFuzzer，Linux/macOS 运行，见 docs/fuzzing.md）。
    /// 契约：Verify 对任何字节序列都不抛异常——CarrierStructureException/CarrierLimitException
    /// 必须被门面消化为诊断与 inconclusive；这里任何异常逃出即为 bug。
    /// </summary>
    public static class Program
    {
        public static void Main(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);
            SharpFuzz.Fuzzer.Run((Stream input) =>
            {
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                VerificationResult result = AigcLabelVerifier.Verify(buffer.ToArray());
                if ((int)result.Verdict is < 1 or > 4)
                {
                    throw new InvalidOperationException($"verdict out of range: {result.Verdict}");
                }
            });
        }
    }
}
