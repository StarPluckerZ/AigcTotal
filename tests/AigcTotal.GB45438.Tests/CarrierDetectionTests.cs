using System;
using AigcTotal.GB45438.Verdict;
using Xunit;

namespace AigcTotal.GB45438.Tests
{
    /// <summary>载体探测层：歧义不静默（polyglot 是对抗输入常态）。</summary>
    public class CarrierDetectionTests
    {
        [Fact]
        public void Polyglot_JpegHeaderPlusFtyp_Ambiguous_Inconclusive()
        {
            // 头 3 字节命中 JPEG 探测器（FF D8 FF），偏移 4..7 命中 MP4 探测器（ftyp）→ 双命中
            byte[] polyglot = { 0xFF, 0xD8, 0xFF, 0xE0, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 0x00, 0x00 };

            var result = AigcLabelVerifier.Verify(polyglot);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
            Assert.Contains(result.Checks, c =>
                c.Check == CheckIds.CarrierDetect && c.Outcome == CheckOutcome.Error
                && c.Code == CheckCodes.AmbiguousFormat);
            Assert.Contains(result.Signals, s => s.Kind == SignalKind.CarrierAmbiguous);
        }

        [Fact]
        public void EmptyInput_TextFallback_NotFound()
        {
            var result = AigcLabelVerifier.Verify(Array.Empty<byte>());

            Assert.Equal(VerdictKind.NotFound, result.Verdict);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.CarrierDetect && c.Outcome == CheckOutcome.Pass);
        }
    }
}
