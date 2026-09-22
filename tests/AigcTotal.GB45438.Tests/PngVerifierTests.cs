using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AigcTotal.GB45438.Verdict;
using AigcTotal.TestSupport;
using Xunit;

namespace AigcTotal.GB45438.Tests
{
    public class PngVerifierTests
    {
        private const string ValidPayload =
            "{\"Label\":\"1\",\"ContentProducer\":\"TestStudio\",\"ProduceID\":\"P-0001\"," +
            "\"ReservedCode1\":\"\",\"ContentPropagator\":\"\",\"PropagateID\":\"\",\"ReservedCode2\":\"\"}";

        private static VerificationResult Verify(byte[] png)
            => AigcLabelVerifier.Verify(png);

        [Fact]
        public void ValidLabel_IsCompliant()
        {
            var png = PngBuilder.Build(
                PngBuilder.Text("AIGC", ValidPayload),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));

            var result = Verify(png);

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Equal(Carriers.CarrierKind.Png, result.Carrier);
            var site = Assert.Single(result.Sites);
            Assert.Equal("1", site.Fields!["Label"]);
            Assert.Equal("TestStudio", site.Fields["ContentProducer"]);
            // 发现层：tEXt 通道 pass，XMP 通道 skip（文件无 iTXt）
            Assert.Contains(result.Checks, c => c.Check == CheckIds.PngTextAigc && c.Outcome == CheckOutcome.Pass);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.PngXmpAigc && c.Outcome == CheckOutcome.Skip);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.AnnexeFields && c.Outcome == CheckOutcome.Pass);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.AnnexeCharset && c.Outcome == CheckOutcome.Pass);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.AnnexeLabelEnum && c.Outcome == CheckOutcome.Pass);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.AnnexeUnknownField && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void NoLabel_IsNotFound()
        {
            var png = PngBuilder.Build(
                PngBuilder.Text("Comment", "hello"),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));

            var result = Verify(png);

            Assert.Equal(VerdictKind.NotFound, result.Verdict);
            Assert.Empty(result.Sites);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.PngTextAigc && c.Outcome == CheckOutcome.Skip);
        }

        [Fact]
        public void MissingField_IsNoncompliant()
        {
            var payload = "{\"Label\":\"1\",\"ContentProducer\":\"TestStudio\"}"; // 缺 ProduceID
            var png = PngBuilder.Build(
                PngBuilder.Text("AIGC", payload),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));

            var result = Verify(png);

            Assert.Equal(VerdictKind.Noncompliant, result.Verdict);
            Assert.Contains(result.Checks, c =>
                c.Check == CheckIds.AnnexeFields && c.Outcome == CheckOutcome.Fail && c.Code == CheckCodes.FieldMissing);
        }

        [Fact]
        public void InvalidLabelEnum_IsNoncompliant()
        {
            var payload = ValidPayload.Replace("\"Label\":\"1\"", "\"Label\":\"9\"");
            var png = PngBuilder.Build(
                PngBuilder.Text("AIGC", payload),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));

            var result = Verify(png);

            Assert.Equal(VerdictKind.Noncompliant, result.Verdict);
            Assert.Contains(result.Checks, c =>
                c.Check == CheckIds.AnnexeLabelEnum && c.Outcome == CheckOutcome.Fail);
        }

        [Fact]
        public void UnknownField_IsNoncompliant_StrictPolicy()
        {
            var payload = ValidPayload.Replace("}", ",\"EvilField\":\"x\"}");
            var png = PngBuilder.Build(
                PngBuilder.Text("AIGC", payload),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));

            var result = Verify(png);

            Assert.Equal(VerdictKind.Noncompliant, result.Verdict);
            Assert.Contains(result.Checks, c =>
                c.Check == CheckIds.AnnexeUnknownField && c.Outcome == CheckOutcome.Fail
                && c.Code == CheckCodes.UnknownField && c.Detail == "EvilField");
        }

        [Fact]
        public void ChineseValue_ViolatesCharset_Noncompliant_PerAnnexeJ()
        {
            // 附录 E j)：字段值限 GB18030 单字节可打印字符——中文名称不合规（应使用编码）
            var payload = ValidPayload.Replace("TestStudio", "测试工作室");
            var png = PngBuilder.Build(
                PngBuilder.Text("AIGC", payload),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));

            var result = Verify(png);

            Assert.Equal(VerdictKind.Noncompliant, result.Verdict);
            Assert.Contains(result.Checks, c =>
                c.Check == CheckIds.AnnexeCharset && c.Outcome == CheckOutcome.Fail
                && c.Code == CheckCodes.CharsetInvalid && c.Detail == "ContentProducer");
        }

        [Fact]
        public void Charset_SpaceAndBackslash_Violate_EscapedQuote_Passes()
        {
            // 空格违规；\" 转义（解码后为引号）合规
            var withSpace = ValidPayload.Replace("TestStudio", "Test Studio");
            var result1 = Verify(PngBuilder.Build(
                PngBuilder.Text("AIGC", withSpace), PngBuilder.Data("IEND", System.Array.Empty<byte>())));
            Assert.Equal(VerdictKind.Noncompliant, result1.Verdict);

            // 源文本 \"Test\" → 解码后含引号 → 按附录 E j) 允许
            var payloadQuote = "{\"Label\":\"1\",\"ContentProducer\":\"\\\"Test\\\"Studio\",\"ProduceID\":\"P-0001\"}";
            var result2 = Verify(PngBuilder.Build(
                PngBuilder.Text("AIGC", payloadQuote), PngBuilder.Data("IEND", System.Array.Empty<byte>())));
            Assert.Equal(VerdictKind.Compliant, result2.Verdict);
        }

        [Fact]
        public void DuplicateIdenticalSites_Noncompliant_Per6_1c_OnlyOneLabel()
        {
            // GB 45438-2025 第 6.1 c)：内容文件中应仅保留一份文件元数据隐式标识
            var png = PngBuilder.Build(
                PngBuilder.Text("AIGC", ValidPayload),
                PngBuilder.Text("AIGC", ValidPayload),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));

            var result = Verify(png);

            Assert.Equal(VerdictKind.Noncompliant, result.Verdict);
            Assert.Equal(2, result.Sites.Count);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.DuplicateLabel && c.Outcome == CheckOutcome.Fail);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.FieldsAgree && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void DisagreeingSites_AreNoncompliant()
        {
            var other = ValidPayload.Replace("P-0001", "P-0002");
            var png = PngBuilder.Build(
                PngBuilder.Text("AIGC", ValidPayload),
                PngBuilder.Text("AIGC", other),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));

            var result = Verify(png);

            Assert.Equal(VerdictKind.Noncompliant, result.Verdict);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.FieldsAgree && c.Outcome == CheckOutcome.Fail);
        }

        [Fact]
        public void CrcMismatch_SignalAndWarn_VerdictFollowsFields()
        {
            var png = PngBuilder.Build(
                PngBuilder.Text("AIGC", ValidPayload),
                PngBuilder.Data("IEND", System.Array.Empty<byte>(), corruptCrc: true));

            var result = Verify(png);

            Assert.Equal(VerdictKind.Compliant, result.Verdict); // M1 基线：字段可读则判定随字段
            Assert.Contains(result.Signals, s => s.Kind == SignalKind.ChecksumMismatch);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.ForensicChecksum && c.Outcome == CheckOutcome.Warn);
        }

        [Fact]
        public void Truncated_NoIend_IsInconclusive()
        {
            var png = PngBuilder.Build(
                PngBuilder.Text("AIGC", ValidPayload));
            var cut = png.Concat(new byte[] { 0x00, 0x01, 0x02 }).ToArray(); // 不足 12 字节的尾巴

            var result = Verify(cut);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
            Assert.Contains(result.Signals, s => s.Kind == SignalKind.StructureTruncated);
        }

        [Fact]
        public void EmptyAigcText_ShellSignal_Inconclusive()
        {
            // keyword=AIGC、负载空：data = "AIGC\0"，无正文 → 空壳
            var png = PngBuilder.Build(
                PngBuilder.Text("AIGC", string.Empty),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));

            var result = Verify(png);

            // 空壳：keyword 在、负载空 → 擦除证据 → 无法判定
            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
            Assert.Contains(result.Signals, s => s.Kind == SignalKind.MetadataShellEmpty);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.ForensicWipe && c.Outcome == CheckOutcome.Error);
        }

        [Fact]
        public void ItxtUnknownKeyword_Ignored_NotFound()
        {
            // keyword 不是 XML:com.adobe.xmp 的 iTXt 与标识无关，正常忽略
            var png = PngBuilder.Build(
                PngBuilder.Data("iTXt", new byte[] { (byte)'X', (byte)'M', (byte)'P', 0, 0, 0, 0, 0 }),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));

            var result = Verify(png);

            Assert.Equal(VerdictKind.NotFound, result.Verdict);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.PngXmpAigc && c.Outcome == CheckOutcome.Skip);
        }

        [Fact]
        public void Png_ItxtXmp_Compliant_WithAnnexeJsonPayload()
        {
            // 豆包实测格式：XMP 包装 + TC260 命名空间 + <TC260:AIGC> 内嵌附录 E JSON
            string xmp =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
                "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                "<rdf:Description rdf:about=\"\" xmlns:TC260=\"http://www.tc260.org.cn/ns/AIGC/1.0/\">" +
                "<TC260:AIGC>{&quot;Label&quot;:&quot;1&quot;,&quot;ContentProducer&quot;:&quot;doubao&quot;," +
                "&quot;ProduceID&quot;:&quot;f123b54e&quot;,&quot;ReservedCode1&quot;:&quot;&quot;," +
                "&quot;ContentPropagator&quot;:&quot;&quot;,&quot;PropagateID&quot;:&quot;&quot;," +
                "&quot;ReservedCode2&quot;:&quot;&quot;}</TC260:AIGC>" +
                "</rdf:Description></rdf:RDF></x:xmpmeta>";
            byte[] itxtData = BuildXmpItxt(xmp);
            var png = PngBuilder.Build(
                PngBuilder.Data("iTXt", itxtData),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));

            var result = Verify(png);

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            var site = Assert.Single(result.Sites);
            Assert.Equal(Carriers.PayloadEncoding.XmpAigc, site.Encoding);
            Assert.Equal("1", site.Fields!["Label"]);
            Assert.Equal("doubao", site.Fields["ContentProducer"]);
            Assert.Equal("f123b54e", site.Fields["ProduceID"]);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.PngXmpAigc && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void Png_ItxtXmp_MalformedPayload_Inconclusive()
        {
            // XMP 关键字在但负载不可解析 → 站点错误 → 无法判定（防假性 not_found）
            byte[] itxtData = BuildXmpItxt("<not-xml");
            var png = PngBuilder.Build(
                PngBuilder.Data("iTXt", itxtData),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));

            var result = Verify(png);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        [Fact]
        public void Png_ItxtXmp_Compressed_NotSupported_Inconclusive()
        {
            // 压缩 iTXt（flag=1）：无法在安全预算内解码 → 畸形信号 → 无法判定
            var data = new List<byte>();
            data.AddRange(Encoding.ASCII.GetBytes("XML:com.adobe.xmp"));
            data.Add(0);
            data.Add(1); // compressionFlag = 1
            data.Add(0); // compressionMethod
            data.Add(0); // language tag 空
            data.Add(0); // translated keyword 空
            data.AddRange(new byte[] { 0x78, 0x9C });
            var png = PngBuilder.Build(
                PngBuilder.Data("iTXt", data.ToArray()),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));

            var result = Verify(png);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
            Assert.Contains(result.Signals, s => s.Kind == SignalKind.StructureMalformed);
        }

        [Fact]
        public void Png_LargeIdat_StreamedCrc_LabelStillFound_UnderTightReadBudget()
        {
            // 单个 5MB IDAT：非元数据块不进内存，标识照常发现
            byte[] bigIdat = new byte[5 * 1024 * 1024];
            for (int i = 0; i < bigIdat.Length; i++) bigIdat[i] = (byte)(i & 0x55);
            var png = PngBuilder.Build(
                PngBuilder.Data("IDAT", bigIdat),
                PngBuilder.Text("AIGC", ValidPayload),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));
            var options = new VerifyOptions { Security = { MaxTotalRead = 64 * 1024 } }; // 远小于 5MB IDAT

            var result = AigcLabelVerifier.Verify(png, options);

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.DoesNotContain(result.Signals, s => s.Kind == SignalKind.ResourceLimitExceeded);
            Assert.Equal(64 * 1024, result.OptionsUsed.MaxTotalRead);
        }

        [Fact]
        public void Png_ManySmallIdats_RealWorldShape_LabelStillFound_UnderTightReadBudget()
        {
            // 复刻豆包 8.8MB 真实形态：上千个 8KB 小 IDAT（每个 ≤ MaxAlloc，但总量超预算）
            byte[] idat = new byte[8 * 1024];
            idat.AsSpan().Fill(0x33);
            var chunks = new List<PngChunk>();
            for (int i = 0; i < 1077; i++)
            {
                chunks.Add(PngBuilder.Data("IDAT", idat));
            }
            chunks.Insert(1, PngBuilder.Text("AIGC", ValidPayload));
            chunks.Add(PngBuilder.Data("IEND", System.Array.Empty<byte>()));
            var png = PngBuilder.Build(chunks.ToArray());
            var options = new VerifyOptions { Security = { MaxTotalRead = 64 * 1024 } };

            var result = AigcLabelVerifier.Verify(png, options);

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.DoesNotContain(result.Signals, s => s.Kind == SignalKind.ResourceLimitExceeded);
        }

        [Fact]
        public void Png_LargeIdat_BadCrc_WarnButVerdictFollowsFields()
        {
            byte[] bigIdat = new byte[5 * 1024 * 1024];
            bigIdat.AsSpan().Fill(0xAA);
            var png = PngBuilder.Build(
                PngBuilder.Data("IDAT", bigIdat, corruptCrc: true),
                PngBuilder.Text("AIGC", ValidPayload),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));

            var result = Verify(png);

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Contains(result.Signals, s => s.Kind == SignalKind.ChecksumMismatch);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.ForensicChecksum && c.Outcome == CheckOutcome.Warn);
        }

        [Fact]
        public void Png_OversizedMetadataChunk_BeyondAlloc_Inconclusive()
        {
            // 元数据块本身超过 MaxAlloc：安全预算内无法验证 → 无法判定
            byte[] oversized = new byte[4096];
            var png = PngBuilder.Build(
                PngBuilder.Text("AIGC", ValidPayload),
                PngBuilder.Data("tEXt", oversized),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));
            var options = new VerifyOptions { Security = { MaxAlloc = 1024 } };

            var result = AigcLabelVerifier.Verify(png, options);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
            Assert.Contains(result.Signals, s => s.Kind == SignalKind.ResourceLimitExceeded);
        }

        /// <summary>构造 iTXt 数据：keyword NUL flag(0) method(0) lang NUL translated NUL text。</summary>
        private static byte[] BuildXmpItxt(string xmp)
        {
            var data = new List<byte>();
            data.AddRange(Encoding.ASCII.GetBytes("XML:com.adobe.xmp"));
            data.Add(0);
            data.Add(0); // compressionFlag
            data.Add(0); // compressionMethod
            data.Add(0); // language tag 空
            data.Add(0); // translated keyword 空
            data.AddRange(Encoding.UTF8.GetBytes(xmp));
            return data.ToArray();
        }

        [Fact]
        public void Png_TextWrappedAigcEnvelope_Compliant_PerTc260Guide()
        {
            // TC260-PG-20259A 附录 B：tEXt 负载为 {"AIGC":{七字段}} 包裹形态
            string wrapped = "{\"AIGC\":" + ValidPayload + "}";
            var png = PngBuilder.Build(
                PngBuilder.Text("AIGC", wrapped),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));

            var result = Verify(png);

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Equal("TestStudio", result.Sites[0].Fields!["ContentProducer"]);
            Assert.Equal("1", result.Sites[0].Fields!["Label"]);
        }

        [Fact]
        public void Determinism_SameInputSameResult()
        {
            var png = PngBuilder.Build(
                PngBuilder.Text("AIGC", ValidPayload),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));

            var a = Verify(png);
            var b = Verify(png);

            Assert.Equal(a.Verdict, b.Verdict);
            Assert.Equal(a.Sites.Count, b.Sites.Count);
            Assert.Equal(a.Sites[0].Fields!["ProduceID"], b.Sites[0].Fields!["ProduceID"]);
            Assert.Equal(a.Checks.Count, b.Checks.Count);
        }

        [Fact]
        public void ResourceLimits_EchoedInResult()
        {
            var png = PngBuilder.Build(PngBuilder.Data("IEND", System.Array.Empty<byte>()));
            var options = new VerifyOptions { Security = { MaxTotalRead = 999 } };

            var result = AigcLabelVerifier.Verify(png, options);

            Assert.Equal(999, result.OptionsUsed.MaxTotalRead);
        }
    }
}
