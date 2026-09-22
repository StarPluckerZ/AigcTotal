using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AigcTotal.GB45438.Verdict;
using AigcTotal.TestSupport;
using Xunit;

namespace AigcTotal.GB45438.Tests
{
    /// <summary>
    /// 不可信输入的防崩溃套件（全平台 CI 可跑的轻量模糊测试）：
    /// 任何输入——变异、截断、长度炸弹、深度炸弹——都不允许有异常逃出 Verify，
    /// 且 verdict 必须落在四档枚举内。正式的覆盖率引导模糊测试见 tools/AigcTotal.Fuzz（Linux）。
    /// </summary>
    public class RobustnessTests
    {
        [Fact]
        public void Corpus_Files_Verify_WithinExpectedVerdicts()
        {
            foreach (byte[] input in BaseCorpus())
            {
                VerificationResult result = AigcLabelVerifier.Verify(input);
                Assert.InRange((int)result.Verdict, 1, 4);
                Assert.NotEmpty(result.Checks);
            }
        }

        [Fact]
        public void RandomMutations_NeverEscapeAndStayWithinVerdicts()
        {
            var random = new Random(Seed: 20260921);
            byte[][] corpus = BaseCorpus();

            for (int iteration = 0; iteration < 400; iteration++)
            {
                byte[] original = corpus[random.Next(corpus.Length)];
                byte[] mutant = (byte[])original.Clone();
                int mutations = 1 + random.Next(8);
                for (int m = 0; m < mutations; m++)
                {
                    if (mutant.Length == 0) break; // 已截断到空；Random.Next(0) 会返回 0 导致越界写
                    switch (random.Next(3))
                    {
                        case 0: // 随机位翻转
                            int bitIndex = random.Next(mutant.Length * 8);
                            mutant[bitIndex / 8] ^= (byte)(1 << (bitIndex % 8));
                            break;
                        case 1: // 随机字节覆写（偏向 0x00/0xFF/边界值）
                            byte[] interesting = { 0x00, 0xFF, 0x7F, 0x80, 0x01 };
                            mutant[random.Next(mutant.Length)] = interesting[random.Next(interesting.Length)];
                            break;
                        case 2: // 截断到随机长度
                            int keep = random.Next(mutant.Length);
                            Array.Resize(ref mutant, keep);
                            break;
                    }
                }
                if (mutant.Length == 0) mutant = new byte[] { 0x00 };

                VerificationResult result = AigcLabelVerifier.Verify(mutant);
                Assert.InRange((int)result.Verdict, 1, 4);
                Assert.NotEmpty(result.Checks);
            }
        }

        [Fact]
        public void Degenerate_NeverEscape()
        {
            var pngLengthBomb = new byte[]
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x7F, 0xFF, 0xFF, 0xFF, // 声明 2GB chunk（> MaxAlloc 4MB → 拒绝）
                (byte)'t', (byte)'E', (byte)'X', (byte)'t',
            };

            var id3SizeBomb = new byte[]
            {
                (byte)'I', (byte)'D', (byte)'3', 0x04, 0x00, 0x00,
                0x7F, 0x7F, 0x7F, 0x7F, // syncsafe 2GB tag
            };

            var mp4LargesizeBomb = BuildBox(new byte[]
            {
                0x00, 0x00, 0x00, 0x01, // size32=1 → largesize
                0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // 2^64-1
                (byte)'f', (byte)'r', (byte)'e', (byte)'e',
            });

            var inputs = new List<byte[]>
            {
                Array.Empty<byte>(),
                new byte[] { 0x89 },
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, // 仅 PNG 签名
                new byte[] { 0xFF, 0xD8, 0xFF },                                // 仅 JPEG 头
                new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' },
                new byte[] { (byte)'I', (byte)'D', (byte)'3' },
                pngLengthBomb,
                id3SizeBomb,
                mp4LargesizeBomb,
                Mp4DepthBomb(depth: 20),
                new byte[] { 0xFF, 0xFE },                       // 仅 UTF-16 LE BOM
                Encoding.ASCII.GetBytes("plain text no bom \xFF\xFE garbage"),
            };

            foreach (byte[] input in inputs)
            {
                VerificationResult result = AigcLabelVerifier.Verify(input);
                Assert.InRange((int)result.Verdict, 1, 4);
                Assert.NotEmpty(result.Checks);
            }
        }

        [Fact]
        public void Mp4DepthBomb_DepthLimitTriggers_Inconclusive()
        {
            byte[] bomb = Mp4DepthBomb(depth: 20);

            VerificationResult result = AigcLabelVerifier.Verify(bomb);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
            Assert.Contains(result.Signals, s => s.Kind == SignalKind.ResourceLimitExceeded);
        }

        [Fact]
        public void PngLengthBomb_AllocLimitTriggers_Inconclusive()
        {
            var bomb = new byte[]
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x7F, 0xFF, 0xFF, 0xFF,
                (byte)'t', (byte)'E', (byte)'X', (byte)'t',
            };

            VerificationResult result = AigcLabelVerifier.Verify(bomb);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        [Fact]
        public void Mp4_XmpEncodingSniffBomb_NoEscape_Inconclusive()
        {
            // SharpFuzz 第三批发现：XMP 声明后跟 0xFF 字节 → BCL XmlReader 编码切换内部
            // 抛 ArgumentOutOfRange（非 XmlException）；信任边界策略：任何下游异常 = 负载畸形
            string xmp = "<?xml version=\"1.0\"\u00FF\u00FF\u00FF\u00FF\u00FF\u00FF";
            byte[] mp4 = Mp4Builder.Build(Mp4Builder.Ftyp("isom"), Mp4Builder.UuidXmp(xmp));

            var result = AigcLabelVerifier.Verify(mp4);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        [Fact]
        public void Mp3_ExtHeaderSkipBomb_NoEscape_Inconclusive()
        {
            // SharpFuzz 第二批发现：ID3v2.3 扩展头 extSize≈2^32 → Seek 越过流边界，
            // MemoryStream 异常逃出契约；修复后 BoundedReader.Seek 防护 → 截断 → 无法判定
            byte[] mp3 = new byte[]
            {
                (byte)'I', (byte)'D', (byte)'3', 0x03, 0x00, 0x40, // v2.3, flags 0x40=扩展头
                0x00, 0x00, 0x00, 0x20,                            // tag size (syncsafe 32)
                0xFF, 0xFF, 0xFF, 0xFF,                            // extSize 炸弹
            };

            var result = AigcLabelVerifier.Verify(mp3);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        [Fact]
        public void Mp4_KeysSizeOverflowBomb_NoEscape_Inconclusive()
        {
            // SharpFuzz 首轮发现的真 bug 回归：keys 条目 keySize ≥ 2^31 时 (int) 强转回绕为负，
            // ReadExactly(负数) 抛 ArgumentOutOfRangeException 逃出门面——修复后应为畸形 → 无法判定
            byte[] keys = Mp4Builder.Box("keys", new byte[]
            {
                0x00, 0x00, 0x00, 0x00,               // version/flags
                0x00, 0x00, 0x00, 0x01,               // entry count = 1
                0xFF, 0xFF, 0xFF, 0xFF,               // keySize = 2^32-1 → (int) 回绕
                (byte)'m', (byte)'d', (byte)'t', (byte)'a',
            });
            byte[] meta = Mp4Builder.Box("meta", new byte[4].Concat(keys).ToArray());
            byte[] mp4 = Mp4Builder.Build(
                Mp4Builder.Ftyp("isom"),
                Mp4Builder.Container("moov", Mp4Builder.Container("udta", meta)));

            var result = AigcLabelVerifier.Verify(mp4);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
            Assert.Contains(result.Signals, s => s.Kind == SignalKind.StructureMalformed);
        }

        /// <summary>嵌套容器 box 深度炸弹：depth 层 moov 套 aigc。</summary>
        private static byte[] Mp4DepthBomb(int depth)
        {
            byte[] inner = Mp4Builder.Box("aigc", new byte[] { 0x7B, 0x7D }); // {}
            for (int i = 0; i < depth; i++)
            {
                inner = Mp4Builder.Container("moov", inner);
            }
            return Mp4Builder.Build(Mp4Builder.Ftyp("isom"), inner);
        }

        private static byte[] BuildBox(byte[] raw) => raw;

        private static byte[][] BaseCorpus()
        {
            const string validJson =
                "{\"Label\":\"1\",\"ContentProducer\":\"RobustStudio\",\"ProduceID\":\"R-9\"," +
                "\"ReservedCode1\":\"\",\"ContentPropagator\":\"\",\"PropagateID\":\"\",\"ReservedCode2\":\"\"}";
            const string validXmp =
                "<?xml version=\"1.0\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\" xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                "<rdf:RDF><rdf:Description Label=\"1\" ContentProducer=\"RobustStudio\" ProduceID=\"R-9\"/></rdf:RDF>" +
                "</x:xmpmeta>";

            return new[]
            {
                PngBuilder.Build(PngBuilder.Text("AIGC", validJson), PngBuilder.Data("IEND", Array.Empty<byte>())),
                PngBuilder.Build(PngBuilder.Data("IEND", Array.Empty<byte>())),
                JpegBuilder.WithXmp(validXmp),
                JpegBuilder.WithoutXmp(),
                Mp4Builder.Build(Mp4Builder.Ftyp("isom"), Mp4Builder.Container("moov", Mp4Builder.UdtaAigc(validJson))),
                WavBuilder.Build(WavBuilder.Fmt(), WavBuilder.Aigc(validJson)),
                Id3Builder.V24(Id3Builder.TxxxFrame("AIGC", validJson)),
                System.Text.Encoding.UTF8.GetBytes("本内容由人工智能生成的文本。"),
            };
        }
    }
}
