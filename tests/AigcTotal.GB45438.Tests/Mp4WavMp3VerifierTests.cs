using System.Linq;
using AigcTotal.GB45438.Verdict;
using AigcTotal.TestSupport;
using Xunit;

namespace AigcTotal.GB45438.Tests
{
    public class Mp4WavMp3VerifierTests
    {
        private const string ValidJson =
            "{\"Label\":\"1\",\"ContentProducer\":\"VideoStudio\",\"ProduceID\":\"V-001\"," +
            "\"ReservedCode1\":\"\",\"ContentPropagator\":\"\",\"PropagateID\":\"\",\"ReservedCode2\":\"\"}";

        private const string ValidXmp =
            "<?xml version=\"1.0\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\" xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:RDF><rdf:Description Label=\"1\" ContentProducer=\"VideoStudio\" ProduceID=\"V-001\"/></rdf:RDF>" +
            "</x:xmpmeta>";

        // —— MP4 ——

        [Fact]
        public void Mp4_UdtaAigcBox_FoundAndCompliant()
        {
            byte[] mp4 = Mp4Builder.Build(
                Mp4Builder.Ftyp("isom"),
                Mp4Builder.Container("moov", Mp4Builder.UdtaAigc(ValidJson)),
                Mp4Builder.Mdat(16));

            var result = AigcLabelVerifier.Verify(mp4);

            Assert.Equal(Carriers.CarrierKind.Mp4, result.Carrier);
            Assert.Equal("isom", result.CarrierDetail);
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.Mp4UdtaAigc && c.Outcome == CheckOutcome.Pass);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.Mp4XmpAigc && c.Outcome == CheckOutcome.Skip);
        }

        [Fact]
        public void Mp4_UdtaMetaKeysIlst_Compliant_PerTc260Guide()
        {
            // TC260-PG-20257A 规定形态：moov.udta.meta.keys + ilst（ffmpeg use_metadata_tags）
            byte[] mp4 = Mp4Builder.Build(
                Mp4Builder.Ftyp("isom"),
                Mp4Builder.Container("moov", Mp4Builder.UdtaMetaAigc(ValidJson)),
                Mp4Builder.Mdat(16));

            var result = AigcLabelVerifier.Verify(mp4);

            Assert.Equal(Carriers.CarrierKind.Mp4, result.Carrier);
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            var site = Assert.Single(result.Sites);
            Assert.Equal("VideoStudio", site.Fields!["ContentProducer"]);
            Assert.Equal(Carriers.PayloadEncoding.Json, site.Encoding);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.Mp4UdtaAigc && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void Mp4_UuidXmpBox_FoundAndCompliant()
        {
            byte[] mp4 = Mp4Builder.Build(
                Mp4Builder.Ftyp("isom"),
                Mp4Builder.UuidXmp(ValidXmp));

            var result = AigcLabelVerifier.Verify(mp4);

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Equal(Carriers.PayloadEncoding.XmpAigc, result.Sites[0].Encoding);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.Mp4XmpAigc && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void Mp4_MdatSkippedWithoutReading_BigFileStillBounded()
        {
            // 大 mdat 后跟 moov：解析只读元数据区，总读取量远小于文件
            byte[] mp4 = Mp4Builder.Build(
                Mp4Builder.Ftyp("isom"),
                Mp4Builder.Mdat(1024 * 1024),
                Mp4Builder.Container("moov", Mp4Builder.UdtaAigc(ValidJson)));

            var options = new VerifyOptions { Security = { MaxTotalRead = 64 * 1024 } }; // 远小于文件
            var result = AigcLabelVerifier.Verify(mp4, options);

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
        }

        [Fact]
        public void Mp4_TruncatedBox_Inconclusive()
        {
            byte[] mp4 = Mp4Builder.Build(Mp4Builder.Ftyp("isom"));
            // 声明 4KB 的 moov box 但无数据字节：完整 8 字节头之后即 EOF → 截断
            var cut = mp4.Concat(new byte[] { 0x00, 0x00, 0x10, 0x00, (byte)'m', (byte)'o', (byte)'o', (byte)'v' }).ToArray();

            var result = AigcLabelVerifier.Verify(cut);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
            Assert.Contains(result.Signals, s => s.Kind == SignalKind.StructureTruncated);
        }

        [Fact]
        public void Mp4_NoLabel_NotFound()
        {
            byte[] mp4 = Mp4Builder.Build(
                Mp4Builder.Ftyp("isom"),
                Mp4Builder.Mdat(8));

            var result = AigcLabelVerifier.Verify(mp4);

            Assert.Equal(VerdictKind.NotFound, result.Verdict);
        }

        // —— WAV ——

        [Fact]
        public void Wav_AigcChunk_FoundAndCompliant()
        {
            byte[] wav = WavBuilder.Build(WavBuilder.Fmt(), WavBuilder.Aigc(ValidJson));

            var result = AigcLabelVerifier.Verify(wav);

            Assert.Equal(Carriers.CarrierKind.Wav, result.Carrier);
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            var site = Assert.Single(result.Sites);
            Assert.Equal("VideoStudio", site.Fields!["ContentProducer"]);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.WavRiffAigc && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void Wav_NoAigcChunk_NotFound()
        {
            byte[] wav = WavBuilder.Build(WavBuilder.Fmt());

            var result = AigcLabelVerifier.Verify(wav);

            Assert.Equal(VerdictKind.NotFound, result.Verdict);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.WavRiffAigc && c.Outcome == CheckOutcome.Skip);
        }

        [Fact]
        public void Wav_EmptyAigcChunk_ShellSignal_Inconclusive()
        {
            byte[] wav = WavBuilder.Build(WavBuilder.Fmt(), ("AIGC", System.Array.Empty<byte>()));

            var result = AigcLabelVerifier.Verify(wav);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
            Assert.Contains(result.Signals, s => s.Kind == SignalKind.MetadataShellEmpty);
        }

        [Fact]
        public void Wav_OddSizedChunkBeforeAigc_PaddingHandled()
        {
            // 奇数长度 fmt（含填充字节）后面的 AIGC chunk 仍能正确定位
            byte[] wav = WavBuilder.Build(("fmt ", new byte[17]), WavBuilder.Aigc(ValidJson));

            var result = AigcLabelVerifier.Verify(wav);

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
        }

        // —— MP3 ——

        [Fact]
        public void Mp3_TxxxAigc_FoundAndCompliant()
        {
            byte[] mp3 = Id3Builder.V24(Id3Builder.TxxxFrame("AIGC", ValidJson));

            var result = AigcLabelVerifier.Verify(mp3);

            Assert.Equal(Carriers.CarrierKind.Mp3, result.Carrier);
            Assert.Equal("ID3 2.4", result.CarrierDetail);
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            var site = Assert.Single(result.Sites);
            Assert.Equal("V-001", site.Fields!["ProduceID"]);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.Mp3Id3Txxx && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void Mp3_OtherTxxx_Ignored_NotFound()
        {
            byte[] mp3 = Id3Builder.V24(
                Id3Builder.TxxxFrame("Comment", "some text"),
                Id3Builder.TxxxFrame("AIGC", ValidJson));

            var result = AigcLabelVerifier.Verify(mp3);

            // AIGC 帧仍在 → 合规
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Single(result.Sites);
        }

        [Fact]
        public void Mp3_NoAigcFrame_NotFound()
        {
            byte[] mp3 = Id3Builder.V24(Id3Builder.TxxxFrame("Comment", "hello"));

            var result = AigcLabelVerifier.Verify(mp3);

            Assert.Equal(VerdictKind.NotFound, result.Verdict);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.Mp3Id3Txxx && c.Outcome == CheckOutcome.Skip);
        }

        [Fact]
        public void Mp3_MalformedJsonInTxxx_PayloadError_Inconclusive()
        {
            byte[] mp3 = Id3Builder.V24(Id3Builder.TxxxFrame("AIGC", "{not json"));

            var result = AigcLabelVerifier.Verify(mp3);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }
    }
}
