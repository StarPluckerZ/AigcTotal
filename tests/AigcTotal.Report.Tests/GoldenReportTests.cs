using System;
using System.IO;
using System.Linq;
using AigcTotal.GB45438;
using AigcTotal.Report;
using Xunit;

namespace AigcTotal.Report.Tests
{
    /// <summary>
    /// schema 冻结执行器：对提交进仓库的全量语料 + golden 报告做逐字节比对（aigc-fixtures golden 生成）。
    /// 任何 schema、canonical 化或判定语义的改动都会在此失败——变更必须是有意识的、经过讨论的
    /// （纪律见 CONTRIBUTING.md；覆盖面矩阵见 README）。
    /// </summary>
    public class GoldenReportTests
    {
        // 与 tools/AigcTotal.Fixtures/Program.cs 的 GoldenReportId/GoldenTimestamp 保持一致（有意冗余：
        // 测试侧显式 pin，防止 fixtures 常量被无意改动后 golden 静默失效）
        private const string PinnedReportId = "01J8GZ3X9QF7Y3N4R5T6Z8A9BC";
        private static readonly DateTimeOffset PinnedTimestamp =
            DateTimeOffset.Parse("2026-09-21T08:30:00Z",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal);

        public static TheoryData<string> GoldenFileNames()
        {
            var data = new TheoryData<string>();
            foreach (string file in Directory.GetFiles(GoldenDir(), "*.report.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(file));
            }
            return data;
        }

        [Theory]
        [MemberData(nameof(GoldenFileNames))]
        public void GoldenReport_ByteIdentical(string goldenName)
        {
            // golden 文件名 = 语料文件名 + ".report.json"
            string corpusName = goldenName.Substring(0, goldenName.Length - ".report.json".Length);
            string corpusPath = Path.Combine(AppContext.BaseDirectory, "corpus", corpusName);
            string goldenPath = Path.Combine(GoldenDir(), goldenName);

            Assert.True(File.Exists(corpusPath), $"corpus missing: {corpusPath}");

            byte[] bytes = File.ReadAllBytes(corpusPath);
            string inputHash = "sha256:" + Hex(System.Security.Cryptography.SHA256.HashData(bytes));

            VerificationResult result = AigcLabelVerifier.Verify(bytes);
            ReportEnvelope envelope = AigcReportBuilder.Build(
                result, inputHash, bytes.Length, "aigc-fixtures", "0.1.0",
                reportId: PinnedReportId, timestampUtc: PinnedTimestamp);

            string golden = File.ReadAllText(goldenPath).TrimEnd('\r', '\n');
            Assert.Equal(golden, envelope.CanonicalJson);
        }

        [Fact]
        public void Golden_Coverage_Floor()
        {
            // 覆盖面地板：四档判定 × 各载体矩阵的 golden 不应意外缩水（当前全量语料 + 种子）
            int count = Directory.GetFiles(GoldenDir(), "*.report.json").Length;
            Assert.True(count >= 40, $"golden reports shrank to {count}");
        }

        private static string GoldenDir() => Path.Combine(AppContext.BaseDirectory, "Golden");

        private static string Hex(byte[] bytes)
        {
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
