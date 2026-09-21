using System;
using System.IO;
using AigcTotal.GB45438;
using AigcTotal.Report;
using AigcTotal.TestSupport;
using Xunit;

namespace AigcTotal.Report.Tests
{
    /// <summary>
    /// schema 冻结执行器：对提交进仓库的语料文件 + golden 报告做逐字节比对。
    /// 任何 schema、canonical 化或判定语义的改动都会在此失败——变更必须是有意识的、经过讨论的。
    /// golden 由 aigc-fixtures（pin 了 report_id/时间戳/工具版本）生成。
    /// </summary>
    public class GoldenReportTests
    {
        [Fact]
        public void GoldenReport_ByteIdentical_ForCommittedCorpus()
        {
            string corpusPath = Path.Combine(AppContext.BaseDirectory, "corpus", "png-label-valid.png");
            string goldenPath = Path.Combine(AppContext.BaseDirectory, "Golden", "png-label-valid.report.json");

            Assert.True(File.Exists(corpusPath), $"corpus missing: {corpusPath}");
            Assert.True(File.Exists(goldenPath), $"golden missing: {goldenPath}");

            byte[] bytes = File.ReadAllBytes(corpusPath);
            string inputHash = "sha256:" + Hex(System.Security.Cryptography.SHA256.HashData(bytes));

            VerificationResult result = AigcLabelVerifier.Verify(bytes);
            ReportEnvelope envelope = AigcReportBuilder.Build(
                result, inputHash, bytes.Length, "aigc-fixtures", "0.1.0",
                reportId: "01J8GZ3X9QF7Y3N4R5T6Z8A9BC",
                timestampUtc: DateTimeOffset.Parse("2026-09-21T08:30:00Z",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal));

            string golden = File.ReadAllText(goldenPath).TrimEnd('\r', '\n');
            Assert.Equal(golden, envelope.CanonicalJson);
        }

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
