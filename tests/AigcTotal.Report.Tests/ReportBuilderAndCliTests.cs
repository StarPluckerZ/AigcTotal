using System;
using System.IO;
using System.Text;
using AigcTotal.GB45438;
using AigcTotal.Report.Cli;
using Xunit;

namespace AigcTotal.Report.Tests
{
    public class ReportBuilderAndCliTests
    {
        private const string ValidPayload =
            "{\"Label\":\"1\",\"ContentProducer\":\"ReportStudio\",\"ProduceID\":\"R-001\"," +
            "\"ReservedCode1\":\"\",\"ContentPropagator\":\"\",\"PropagateID\":\"\",\"ReservedCode2\":\"\"}";

        private static byte[] ValidPng()
        {
            return AigcTotal.TestSupport.PngBuilder.Build(
                AigcTotal.TestSupport.PngBuilder.Text("AIGC", ValidPayload),
                AigcTotal.TestSupport.PngBuilder.Data("IEND", Array.Empty<byte>()));
        }

        [Fact]
        public void Envelope_CanonicalForm_MatchesFrozenSchema()
        {
            byte[] png = ValidPng();
            var result = AigcLabelVerifier.Verify(png);
            string inputHash = "sha256:" + Hex(System.Security.Cryptography.SHA256.HashData(png));

            ReportEnvelope envelope = AigcReportBuilder.Build(
                result, inputHash, png.Length, "aigc-report", "0.1.0",
                reportId: "01J8GZ3X9QF7Y3N4R5T6Z8A9BC", timestampUtc: DateTimeOffset.Parse("2026-09-21T08:30:00Z", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal));

            // 键按 JCS 排序：checks < input < options < report_id < signals < sites < timestamp < tool < verdict < version
            Assert.Contains("\"checks\":[", envelope.CanonicalJson);
            int iChecks = envelope.CanonicalJson.IndexOf("\"checks\"", StringComparison.Ordinal);
            int iInput = envelope.CanonicalJson.IndexOf("\"input\"", StringComparison.Ordinal);
            int iOptions = envelope.CanonicalJson.IndexOf("\"options\"", StringComparison.Ordinal);
            int iReportId = envelope.CanonicalJson.IndexOf("\"report_id\"", StringComparison.Ordinal);
            int iSites = envelope.CanonicalJson.IndexOf("\"sites\"", StringComparison.Ordinal);
            int iTimestamp = envelope.CanonicalJson.IndexOf("\"timestamp\"", StringComparison.Ordinal);
            int iVerdict = envelope.CanonicalJson.IndexOf("\"verdict\"", StringComparison.Ordinal);
            // 顶层 version 用 LastIndexOf：首次出现位于 tool 对象内部（序列化在 verdict 之前）
            int iVersion = envelope.CanonicalJson.LastIndexOf("\"version\"", StringComparison.Ordinal);
            Assert.True(iChecks < iInput);
            Assert.True(iInput < iOptions);
            Assert.True(iOptions < iReportId);
            Assert.True(iReportId < iSites);
            Assert.True(iSites < iTimestamp);
            Assert.True(iTimestamp < iVerdict);
            Assert.True(iVerdict < iVersion);
            Assert.EndsWith("\"verdict\":\"compliant\",\"version\":1}", envelope.CanonicalJson);

            Assert.Contains("\"verdict\":\"compliant\"", envelope.CanonicalJson);
            Assert.Contains("\"mime\":\"image/png\"", envelope.CanonicalJson);
            Assert.Contains("\"carrier\":\"png\"", envelope.CanonicalJson);
            Assert.Contains("\"ReportStudio\"", envelope.CanonicalJson);
            Assert.Contains("\"timestamp\":\"2026-09-21T08:30:00Z\"", envelope.CanonicalJson);
            Assert.Contains("\"report_id\":\"01J8GZ3X9QF7Y3N4R5T6Z8A9BC\"", envelope.CanonicalJson);
            Assert.Contains("\"sha256\":" + "\"" + inputHash + "\"", envelope.CanonicalJson);
            Assert.StartsWith("sha256:", envelope.ReportSha256);
            Assert.Equal(64 + "sha256:".Length, envelope.ReportSha256.Length);
        }

        [Fact]
        public void Deterministic_ContentParts_GivenSameIdAndTs()
        {
            byte[] png = ValidPng();
            var result = AigcLabelVerifier.Verify(png);
            string hash = "sha256:" + Hex(System.Security.Cryptography.SHA256.HashData(png));

            var a = AigcReportBuilder.Build(result, hash, png.Length, "aigc-report", "0.1.0",
                reportId: "01J8GZ3X9QF7Y3N4R5T6Z8A9BC", timestampUtc: DateTimeOffset.Parse("2026-09-21T08:30:00Z", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal));
            var b = AigcReportBuilder.Build(result, hash, png.Length, "aigc-report", "0.1.0",
                reportId: "01J8GZ3X9QF7Y3N4R5T6Z8A9BC", timestampUtc: DateTimeOffset.Parse("2026-09-21T08:30:00Z", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal));

            Assert.Equal(a.CanonicalJson, b.CanonicalJson);
            Assert.Equal(a.ReportSha256, b.ReportSha256);
        }

        [Fact]
        public void ReportSha256_Matches_ManualRehash()
        {
            byte[] png = ValidPng();
            var result = AigcLabelVerifier.Verify(png);
            string hash = "sha256:" + Hex(System.Security.Cryptography.SHA256.HashData(png));

            var envelope = AigcReportBuilder.Build(result, hash, png.Length, "aigc-report", "0.1.0");

            string manual = "sha256:" + Hex(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(envelope.CanonicalJson)));
            Assert.Equal(manual, envelope.ReportSha256);
        }

        // —— CLI ——

        [Fact]
        public void Cli_Stdout_CompliantFile_ExitsZero()
        {
            string dir = Path.Combine(Path.GetTempPath(), "aigc-cli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string file = Path.Combine(dir, "sample.png");
                File.WriteAllBytes(file, ValidPng());

                var stdout = new StringWriter();
                var stderr = new StringWriter();
                string[] args = { "--stdout", file };
                int exit = ReportCli.Run(args, stdout, stderr);

                Assert.Equal(0, exit);
                Assert.Contains("\"verdict\":\"compliant\"", stdout.ToString());
                Assert.Contains("report=sha256:", stderr.ToString());
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Cli_NoLabelFile_ExitsTwo_WritesReportFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "aigc-cli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string file = Path.Combine(dir, "plain.png");
                File.WriteAllBytes(file, AigcTotal.TestSupport.PngBuilder.Build(
                    AigcTotal.TestSupport.PngBuilder.Data("IEND", Array.Empty<byte>())));

                var stdout = new StringWriter();
                var stderr = new StringWriter();
                string[] args = { file };
                int exit = ReportCli.Run(args, stdout, stderr);

                Assert.Equal(2, exit); // not_found
                string reportPath = file + ".report.json";
                Assert.True(File.Exists(reportPath));
                Assert.Contains("\"verdict\":\"not_found\"", File.ReadAllText(reportPath));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Cli_MissingFile_ExitsTen()
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            string[] args = { "no-such-file.png" };
            int exit = ReportCli.Run(args, stdout, stderr);

            Assert.Equal(10, exit);
            Assert.Contains("file not found", stderr.ToString());
        }

        [Fact]
        public void Cli_StdoutWithOutDir_Conflict_ExitsTen()
        {
            string file = Path.Combine(Path.GetTempPath(), "aigc-cli-" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(file, ValidPng());
            try
            {
                var stdout = new StringWriter();
                var stderr = new StringWriter();
                string[] args = { "--stdout", "--out", Path.GetTempPath(), file };
                int exit = ReportCli.Run(args, stdout, stderr);

                Assert.Equal(10, exit);
                Assert.Contains("mutually exclusive", stderr.ToString());
            }
            finally
            {
                File.Delete(file);
            }
        }

        // —— MIME 映射（brand 由解析器剥过尾部填充空格）——

        [Fact]
        public void Mime_QuickTimeBrand_MapsToVideoQuicktime()
        {
            byte[] mov = AigcTotal.TestSupport.Mp4Builder.Build(
                AigcTotal.TestSupport.Mp4Builder.Ftyp("qt  "), // QuickTime 实写形态（含填充）
                AigcTotal.TestSupport.Mp4Builder.Container("moov",
                    AigcTotal.TestSupport.Mp4Builder.UdtaAigc(ValidPayload)));
            var result = AigcLabelVerifier.Verify(mov);
            Assert.Equal("qt", result.CarrierDetail);

            string hash = "sha256:" + Hex(System.Security.Cryptography.SHA256.HashData(mov));
            var envelope = AigcReportBuilder.Build(result, hash, mov.Length, "aigc-report", "0.1.0");

            Assert.Contains("\"mime\":\"video/quicktime\"", envelope.CanonicalJson);
            Assert.Contains("\"carrier_detail\":\"qt\"", envelope.CanonicalJson);
        }

        [Fact]
        public void Mime_M4aBrand_MapsToAudioMp4()
        {
            byte[] m4a = AigcTotal.TestSupport.Mp4Builder.Build(
                AigcTotal.TestSupport.Mp4Builder.Ftyp("M4A "),
                AigcTotal.TestSupport.Mp4Builder.Container("moov",
                    AigcTotal.TestSupport.Mp4Builder.UdtaAigc(ValidPayload)));

            var result = AigcLabelVerifier.Verify(m4a);
            string hash = "sha256:" + Hex(System.Security.Cryptography.SHA256.HashData(m4a));
            var envelope = AigcReportBuilder.Build(result, hash, m4a.Length, "aigc-report", "0.1.0");

            Assert.Contains("\"mime\":\"audio/mp4\"", envelope.CanonicalJson);
        }

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
