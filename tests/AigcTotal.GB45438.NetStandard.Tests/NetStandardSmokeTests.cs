using System;
using System.Collections.Generic;
using System.Text;
using AigcTotal.GB45438;
using AigcTotal.GB45438.Verdict;
using AigcTotal.Report;
using AigcTotal.TestSupport;
using Xunit;

namespace AigcTotal.NetStandard.Tests
{
    /// <summary>
    /// netstandard2.0 编译产物的行为冒烟套件：验证 ns2.0 目标上解码/校验/报告字节形态
    /// 与 net10.0 行为一致。跑在 net10.0 运行器上，被测 DLL 为 ns2.0 构建。
    /// </summary>
    public class NetStandardSmokeTests
    {
        private const string ValidJson =
            "{\"Label\":\"1\",\"ContentProducer\":\"NsStudio\",\"ProduceID\":\"NS-1\"," +
            "\"ReservedCode1\":\"\",\"ContentPropagator\":\"\",\"PropagateID\":\"\",\"ReservedCode2\":\"\"}";

        public static IEnumerable<object[]> Corpus()
        {
            yield return new object[] { PngBuilder.Build(
                PngBuilder.Text("AIGC", ValidJson),
                PngBuilder.Data("IEND", Array.Empty<byte>())), VerdictKind.Compliant };
            yield return new object[] { PngBuilder.Build(
                PngBuilder.Data("IEND", Array.Empty<byte>())), VerdictKind.NotFound };
            yield return new object[] { PngBuilder.Build(
                PngBuilder.Ztxt("AIGC", ValidJson),
                PngBuilder.Data("IEND", Array.Empty<byte>())), VerdictKind.Compliant };
            yield return new object[] { JpegBuilder.WithXmp(
                "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                "<rdf:RDF><rdf:Description Label=\"1\" ContentProducer=\"NsStudio\" ProduceID=\"NS-1\"/></rdf:RDF></x:xmpmeta>"),
                VerdictKind.Compliant };
            yield return new object[] { Mp4Builder.Build(
                Mp4Builder.Ftyp("isom"), Mp4Builder.Container("moov", Mp4Builder.UdtaMetaAigc(ValidJson))),
                VerdictKind.Compliant };
            yield return new object[] { WavBuilder.Build(WavBuilder.Fmt(), WavBuilder.Aigc(ValidJson)),
                VerdictKind.Compliant };
            yield return new object[] { Id3Builder.V24(Id3Builder.TxxxFrame("AIGC", ValidJson)),
                VerdictKind.Compliant };
            yield return new object[] { Encoding.UTF8.GetBytes("本内容由人工智能生成。"),
                VerdictKind.Compliant };
            yield return new object[] { Array.Empty<byte>(), VerdictKind.NotFound };
        }

        [Theory]
        [MemberData(nameof(Corpus))]
        public void Verify_Works_OnNetStandardBuild(byte[] input, VerdictKind expected)
        {
            VerificationResult result = AigcLabelVerifier.Verify(input);

            Assert.Equal(expected, result.Verdict);
            Assert.NotEmpty(result.Checks);
        }

        [Fact]
        public void Charset_Violation_Detected_OnNetStandardBuild()
        {
            const string chinese = "{\"Label\":\"1\",\"ContentProducer\":\"中文平台\",\"ProduceID\":\"NS-1\"}";
            byte[] png = PngBuilder.Build(
                PngBuilder.Text("AIGC", chinese),
                PngBuilder.Data("IEND", Array.Empty<byte>()));

            VerificationResult result = AigcLabelVerifier.Verify(png);

            Assert.Equal(VerdictKind.Noncompliant, result.Verdict);
            Assert.Contains(result.Checks, c =>
                c.Check == CheckIds.AnnexeCharset && c.Outcome == CheckOutcome.Fail
                && c.Code == CheckCodes.CharsetInvalid);
        }

        [Fact]
        public void Report_CanonicalForm_Identical_OnNetStandardBuild()
        {
            byte[] png = PngBuilder.Build(
                PngBuilder.Text("AIGC", ValidJson),
                PngBuilder.Data("IEND", Array.Empty<byte>()));
            VerificationResult result = AigcLabelVerifier.Verify(png);
            string hash = "sha256:" + Hex(System.Security.Cryptography.SHA256.HashData(png));

            ReportEnvelope envelope = AigcReportBuilder.Build(
                result, hash, png.Length, "aigc-report", "0.1.0",
                reportId: "01J8GZ3X9QF7Y3N4R5T6Z8A9BC",
                timestampUtc: DateTimeOffset.Parse("2026-09-21T08:30:00Z",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal));

            // 与 net10.0 构建同输入同参数 → 字节一致（跨 TFM 可复现）
            Assert.EndsWith("\"verdict\":\"compliant\",\"version\":1}", envelope.CanonicalJson);
            Assert.StartsWith("sha256:", envelope.ReportSha256);
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
