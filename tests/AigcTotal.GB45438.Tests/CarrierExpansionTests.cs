using System;
using System.Linq;
using System.Text;
using AigcTotal.GB45438.Verdict;
using AigcTotal.TestSupport;
using Xunit;

namespace AigcTotal.GB45438.Tests
{
    /// <summary>
    /// 载体横向扩张（TC260 四份指南规定的写入位置）：FLAC / OGG / AVI / WebP / TIFF / GIF /
    /// OOXML（docx 族）/ PDF / Markdown front matter。每个载体：合规 happy path + 未检出。
    /// M4A 不单列——与 MP4 同为 ftyp/isom 容器 + keys/ilst 机制，Mp4Parser 已覆盖。
    /// </summary>
    public class CarrierExpansionTests
    {
        private const string ValidJson =
            "{\"Label\":\"1\",\"ContentProducer\":\"WideStudio\",\"ProduceID\":\"W-001\"," +
            "\"ReservedCode1\":\"\",\"ContentPropagator\":\"\",\"PropagateID\":\"\",\"ReservedCode2\":\"\"}";

        private const string ValidXmp =
            "<?xml version=\"1.0\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\" xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:RDF><rdf:Description Label=\"1\" ContentProducer=\"WideStudio\" ProduceID=\"W-001\"/></rdf:RDF>" +
            "</x:xmpmeta>";

        [Fact]
        public void Flac_VorbisComment_Compliant()
        {
            var result = AigcLabelVerifier.Verify(FlacBuilder.Build(ValidJson));

            Assert.Equal(Carriers.CarrierKind.Flac, result.Carrier);
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Equal("W-001", result.Sites[0].Fields!["ProduceID"]);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.FlacVorbisAigc && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void Flac_NoAigcEntry_NotFound()
        {
            // 非 AIGC 前缀的 Vorbis 注释与标识无关
            var result = AigcLabelVerifier.Verify(FlacBuilder.Build("{\"Other\":\"x\"}", withAigcPrefix: false));
            Assert.Equal(VerdictKind.NotFound, result.Verdict);
        }

        [Fact]
        public void Avi_NoAigcSubchunk_NotFound_Fixed()
        {
            byte[] avi = AviBuilder.Build(null);
            var result = AigcLabelVerifier.Verify(avi);
            Assert.Equal(VerdictKind.NotFound, result.Verdict);
        }

        [Fact]
        public void Ogg_VorbisComment_Compliant()
        {
            var result = AigcLabelVerifier.Verify(OggBuilder.Build(ValidJson));

            Assert.Equal(Carriers.CarrierKind.Ogg, result.Carrier);
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Equal("WideStudio", result.Sites[0].Fields!["ContentProducer"]);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.OggCommentAigc && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void Avi_ListInfo_Compliant()
        {
            var result = AigcLabelVerifier.Verify(AviBuilder.Build(ValidJson));

            Assert.Equal(Carriers.CarrierKind.Avi, result.Carrier);
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Equal("W-001", result.Sites[0].Fields!["ProduceID"]);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.AviRiffAigc && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void Webp_XmpChunk_Compliant()
        {
            var result = AigcLabelVerifier.Verify(WebpBuilder.Build(ValidXmp));

            Assert.Equal(Carriers.CarrierKind.Webp, result.Carrier);
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Equal(Carriers.PayloadEncoding.XmpAigc, result.Sites[0].Encoding);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.WebpXmpAigc && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void Tiff_Ifd0Tag2bc_Compliant()
        {
            var result = AigcLabelVerifier.Verify(TiffBuilder.Build(ValidXmp));

            Assert.Equal(Carriers.CarrierKind.Tiff, result.Carrier);
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Equal("W-001", result.Sites[0].Fields!["ProduceID"]);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.TiffIfd0Aigc && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void Gif_AppExtensionXmp_Compliant()
        {
            var result = AigcLabelVerifier.Verify(GifBuilder.Build(ValidXmp));

            Assert.Equal(Carriers.CarrierKind.Gif, result.Carrier);
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Equal(Carriers.PayloadEncoding.XmpAigc, result.Sites[0].Encoding);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.GifAppExtAigc && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void Ooxml_CustomProperty_Compliant()
        {
            var result = AigcLabelVerifier.Verify(OoxmlBuilder.Build(ValidJson));

            Assert.Equal(Carriers.CarrierKind.Ooxml, result.Carrier);
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Equal("WideStudio", result.Sites[0].Fields!["ContentProducer"]);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.OoxmlCustomAigc && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void Pdf_InfoDict_Compliant()
        {
            var result = AigcLabelVerifier.Verify(PdfBuilder.Build(ValidJson));

            Assert.Equal(Carriers.CarrierKind.Pdf, result.Carrier);
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Equal("W-001", result.Sites[0].Fields!["ProduceID"]);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.PdfInfoAigc && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void Pdf_NoAigcKey_NotFound()
        {
            byte[] pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj<</Title(x)>>endobj\n%%EOF");

            var result = AigcLabelVerifier.Verify(pdf);

            Assert.Equal(VerdictKind.NotFound, result.Verdict);
        }

        [Fact]
        public void Markdown_FrontMatter_Compliant()
        {
            byte[] md = Encoding.UTF8.GetBytes(MdBuilder.FrontMatter("1", "MdStudio", "M-1"));

            var result = AigcLabelVerifier.Verify(md);

            Assert.Equal(Carriers.CarrierKind.Text, result.Carrier);
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Equal(Carriers.PayloadEncoding.FrontMatterYaml, result.Sites[0].Encoding);
            Assert.Equal("MdStudio", result.Sites[0].Fields!["ContentProducer"]);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.TextFrontMatterAigc && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void Markdown_FrontMatterMissingLabel_Noncompliant()
        {
            // front matter 有 AIGC 块但缺 ProduceID → 附录 E 字段缺失 → 不合规
            byte[] md = Encoding.UTF8.GetBytes("---\nAIGC:\n  Label: '1'\n  ContentProducer: 'MdStudio'\n---\n\n正文");

            var result = AigcLabelVerifier.Verify(md);

            Assert.Equal(VerdictKind.Noncompliant, result.Verdict);
            Assert.Contains(result.Checks, c =>
                c.Check == CheckIds.AnnexeFields && c.Outcome == CheckOutcome.Fail);
        }

        [Fact]
        public void M4a_Brand_UsesMp4Parser()
        {
            // M4A 与 MP4 同为 BMFF + keys/ilst（TC260-PG-202510A），CarrierKind.Mp4 覆盖
            byte[] m4a = Mp4Builder.Build(
                Mp4Builder.Ftyp("M4A "),
                Mp4Builder.Container("moov", Mp4Builder.UdtaMetaAigc(ValidJson)));

            var result = AigcLabelVerifier.Verify(m4a);

            Assert.Equal(Carriers.CarrierKind.Mp4, result.Carrier);
            Assert.Equal("M4A ", result.CarrierDetail);
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
        }
    }
}
