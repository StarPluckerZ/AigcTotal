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
                new byte[] { 0x66, 0x4C, 0x61, 0x43 },           // 仅 fLaC
                new byte[] { 0x4F, 0x67, 0x67, 0x53 },           // 仅 OggS
                Encoding.ASCII.GetBytes("RIFF\0\0\0\0AVI "),     // 仅 RIFF/AVI 签名
                Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBP"),     // 仅 RIFF/WEBP 签名
                new byte[] { 0x49, 0x49, 0x2A, 0x00 },           // 仅 TIFF II 签名
                new byte[] { 0x4D, 0x4D, 0x00, 0x2A },           // 仅 TIFF MM 签名
                Encoding.ASCII.GetBytes("GIF8"),                 // 仅 GIF 签名
                new byte[] { 0x50, 0x4B, 0x03, 0x04 },           // 仅 ZIP local header
                Encoding.ASCII.GetBytes("%PDF-"),                // 仅 PDF 签名
                Encoding.ASCII.GetBytes("---"),                  // 仅 front matter 定界符
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

        // —— FLAC ——

        [Fact]
        public void Flac_EofInBlockHeaderBomb_NoEscape_Inconclusive()
        {
            // 原 OOXML/OGG 同款缺陷回归：块头逐字节 ReadAtMost(1)[0]，EOF 落在头内 → 空数组越界逃出门面；
            // 修复后整体 ReadExactly(4) → 截断诊断
            var bomb = new byte[] { 0x66, 0x4C, 0x61, 0x43, 0x00 }; // fLaC + 块头首字节后截断

            var result = AigcLabelVerifier.Verify(bomb);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        [Fact]
        public void Flac_BlockLenSeekBomb_NoEscape_Inconclusive()
        {
            // 块头声明 16MB 长度但文件只有头：Seek 越过 EOF → 截断诊断（非越界异常）
            var bomb = new byte[]
            {
                0x66, 0x4C, 0x61, 0x43,          // fLaC
                0x00, 0xFF, 0xFF, 0xFF,          // 类型 0（STREAMINFO），长度 0xFFFFFF，非末块
            };

            var result = AigcLabelVerifier.Verify(bomb);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        [Fact]
        public void Flac_StructuresCountBomb_LimitTriggers_Inconclusive()
        {
            // 10 万零长度块刷爆 MaxStructures：资源上限诊断终止，不崩不挂死
            var bomb = new List<byte> { 0x66, 0x4C, 0x61, 0x43 };
            byte[] block = { 0x01, 0x00, 0x00, 0x00 }; // 类型 1，长度 0，非末块
            for (int i = 0; i <= 100_000; i++)
            {
                bomb.AddRange(block);
            }

            var result = AigcLabelVerifier.Verify(bomb.ToArray());

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
            Assert.Contains(result.Signals, s => s.Kind == SignalKind.ResourceLimitExceeded);
        }

        // —— OGG ——

        [Fact]
        public void Ogg_EofInPageHeaderBomb_NoEscape_Inconclusive()
        {
            // 页固定头 26 字节后在 segment count 字节前截断：原 ReadAtMost(1)[0] 越界（回归用例）
            var bomb = new byte[26];
            bomb[0] = 0x4F; bomb[1] = 0x67; bomb[2] = 0x67; bomb[3] = 0x53; // OggS

            var result = AigcLabelVerifier.Verify(bomb);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        [Fact]
        public void Ogg_SegmentTableTruncationBomb_NoEscape_Inconclusive()
        {
            // segment count 声明 255 段但表只有 10 字节：ReadExactly → 截断诊断
            var bomb = new byte[27 + 10]; // 页头 27 字节（含 count）+ 残缺表
            bomb[0] = 0x4F; bomb[1] = 0x67; bomb[2] = 0x67; bomb[3] = 0x53;
            bomb[26] = 0xFF;

            var result = AigcLabelVerifier.Verify(bomb);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        // —— AVI / WebP（RIFF 系） ——

        [Fact]
        public void Avi_ChunkSizeBeyondRiffBomb_NoEscape_Inconclusive()
        {
            // chunk 声明长度越过 RIFF 边界 → StructureTruncated 信号终止遍历（非越界异常）
            var bomb = new List<byte>();
            bomb.AddRange(Encoding.ASCII.GetBytes("RIFF"));
            bomb.AddRange(Bytes.U32LE(16)); // RIFF size 覆盖后续全部 16 字节
            bomb.AddRange(Encoding.ASCII.GetBytes("AVI "));
            bomb.AddRange(Encoding.ASCII.GetBytes("JUNK"));
            bomb.AddRange(Bytes.U32LE(0x7FFFFF00u)); // 声明长度远超 RIFF 尾
            bomb.AddRange(new byte[4]);

            var result = AigcLabelVerifier.Verify(bomb.ToArray());

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        [Fact]
        public void Webp_ChunkSizeBeyondRiffBomb_NoEscape_Inconclusive()
        {
            // XMP chunk 声明长度越过 RIFF 边界 → StructureTruncated 信号终止遍历
            var bomb = new List<byte>();
            bomb.AddRange(Encoding.ASCII.GetBytes("RIFF"));
            bomb.AddRange(Bytes.U32LE(16));
            bomb.AddRange(Encoding.ASCII.GetBytes("WEBP"));
            bomb.AddRange(Encoding.ASCII.GetBytes("XMP "));
            bomb.AddRange(Bytes.U32LE(0x7FFFFF00u));
            bomb.AddRange(new byte[4]);

            var result = AigcLabelVerifier.Verify(bomb.ToArray());

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        // —— TIFF ——

        [Fact]
        public void Tiff_ValueOffsetBeyondEofBomb_NoEscape_Inconclusive()
        {
            // XMP 条目 count=0xFFFFFF（16MB）：值偏移+长度越过文件 → StructureTruncated 信号
            var bomb = new List<byte>();
            bomb.AddRange(new byte[] { 0x49, 0x49, 0x2A, 0x00 }); // II + 42
            bomb.AddRange(Bytes.U32LE(8));                        // IFD0 偏移
            bomb.AddRange(new byte[] { 1, 0 });                   // 条目数 = 1
            bomb.AddRange(new byte[] { 0xBC, 0x02 });             // tag 0x2BC（XMP）
            bomb.AddRange(new byte[] { 7, 0 });                   // type 7 UNDEFINED
            bomb.AddRange(Bytes.U32LE(0x00FFFFFFu));              // count
            bomb.AddRange(Bytes.U32LE(26));                       // value 偏移
            bomb.AddRange(Bytes.U32LE(0));                        // 下一 IFD

            var result = AigcLabelVerifier.Verify(bomb.ToArray());

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        [Fact]
        public void Tiff_EntryCountBomb_NoEscape_Inconclusive()
        {
            // IFD 条目数 0xFFFF 超过 1 万上限 → 畸形信号 + 跳过条目遍历
            var bomb = new List<byte>();
            bomb.AddRange(new byte[] { 0x49, 0x49, 0x2A, 0x00 });
            bomb.AddRange(Bytes.U32LE(8));
            bomb.AddRange(new byte[] { 0xFF, 0xFF });

            var result = AigcLabelVerifier.Verify(bomb.ToArray());

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        // —— GIF ——

        [Fact]
        public void Gif_EofInAppExtSizeBomb_NoEscape_Inconclusive()
        {
            // 0x21 0xFF 后无块长字节即 EOF：原 ReadAtMost(1)[0] 越界（回归用例）
            var bomb = new List<byte>();
            bomb.AddRange(Encoding.ASCII.GetBytes("GIF89a"));
            bomb.AddRange(new byte[] { 1, 0, 1, 0, 0x00, 0, 0 });
            bomb.AddRange(new byte[] { 0x21, 0xFF });

            var result = AigcLabelVerifier.Verify(bomb.ToArray());

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        [Fact]
        public void Gif_SubBlockEofBomb_NoEscape_Inconclusive()
        {
            // 注释扩展子块声明 5 字节、数据恰好到 EOF：下一 len 字节读到 EOF → 截断信号（回归用例）
            var bomb = new List<byte>();
            bomb.AddRange(Encoding.ASCII.GetBytes("GIF89a"));
            bomb.AddRange(new byte[] { 1, 0, 1, 0, 0x00, 0, 0 });
            bomb.AddRange(new byte[] { 0x21, 0xFE, 0x05 }); // 注释扩展，子块 5 字节
            bomb.AddRange(new byte[] { 1, 2, 3, 4, 5 });

            var result = AigcLabelVerifier.Verify(bomb.ToArray());

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        [Fact]
        public void Gif_XmpSubBlockEofBomb_NoEscape_Inconclusive()
        {
            // SharpFuzz 第四批发现（手动 run 20260922）：XMP Data 扩展的子块链在 EOF 截断——
            // 读 len 字节处 ReadAtMost(1)[0] 空数组越界逃出门面；修复后 → 截断信号 → 无法判定
            var bomb = new List<byte>();
            bomb.AddRange(Encoding.ASCII.GetBytes("GIF89a"));
            bomb.AddRange(new byte[] { 1, 0, 1, 0, 0x00, 0, 0 });
            bomb.AddRange(new byte[] { 0x21, 0xFF, 0x0B }); // 应用扩展，块长 11
            bomb.AddRange(Encoding.ASCII.GetBytes("XMP Data")); // 标识符 8 字节
            bomb.AddRange(new byte[] { 0xEC, 0x1B, 0xDF });     // 鉴别码
            bomb.AddRange(new byte[] { 0x04, 0x7B, 0x7D, 0x7B, 0x7D }); // 一个 4 字节子块后立即 EOF

            var result = AigcLabelVerifier.Verify(bomb.ToArray());

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        [Fact]
        public void Gif_GlobalColorTableSeekBomb_NoEscape_Inconclusive()
        {
            // packed 声明 256 项全局色表但文件在描述符后即截断 → Seek 越界 → 截断诊断
            var bomb = new List<byte>();
            bomb.AddRange(Encoding.ASCII.GetBytes("GIF89a"));
            bomb.AddRange(new byte[] { 1, 0, 1, 0, 0x87, 0, 0 }); // packed=0x87：GCT 存在、256 项

            var result = AigcLabelVerifier.Verify(bomb.ToArray());

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        // —— OOXML ——

        [Fact]
        public void Ooxml_DeflateBomb_AllocLimitTriggers_Inconclusive()
        {
            // ZIP 炸弹：8MB 零 → ~8KB deflate 密文，解压越过 MaxAlloc → 资源上限诊断（防 OOM）
            byte[] compressed = OoxmlBuilder.Deflate(new byte[8 * 1024 * 1024]);
            byte[] bomb = OoxmlBuilder.BuildRaw(compressed, method: 8, usize: 8 * 1024 * 1024);

            var result = AigcLabelVerifier.Verify(bomb);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
            Assert.Contains(result.Signals, s => s.Kind == SignalKind.ResourceLimitExceeded);
        }

        [Fact]
        public void Ooxml_CorruptDeflateBomb_NoEscape_Inconclusive()
        {
            // method=8 但负载为垃圾字节：DeflateStream InvalidDataException → 负载畸形，不逃逸
            byte[] bomb = OoxmlBuilder.BuildRaw(new byte[] { 0x01, 0xFF, 0xFE, 0x00, 0x37, 0x21 }, method: 8);

            var result = AigcLabelVerifier.Verify(bomb);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        [Fact]
        public void Ooxml_TruncatedEntryBomb_NoEscape_Inconclusive()
        {
            // csize 声明完整条目但文件在条目数据中段截断 → ReadExactly 截断诊断
            byte[] full = OoxmlBuilder.Build("{\"Label\":\"1\"}");
            byte[] bomb = full[..^20];

            var result = AigcLabelVerifier.Verify(bomb);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        [Fact]
        public void Ooxml_DescriptorTailBomb_LimitTriggers_Inconclusive()
        {
            // 无中央目录 → 回退本地头遍历；bit3 + csize=0 → 描述符发现路径；
            // 5MB 无任何 PK 签名的尾越过 MaxAlloc → 资源上限诊断。
            // 原实现对此输入做逐字节 Seek+ReadExactly 扫描（数百万次流调用）——DoS 回归用例
            var bomb = new List<byte>();
            bomb.AddRange(new byte[] { 0x50, 0x4B, 0x03, 0x04 });
            bomb.AddRange(new byte[] { 20, 0 });        // version
            bomb.AddRange(new byte[] { 8, 0 });         // flags bit3 = 描述符
            bomb.AddRange(new byte[] { 0, 0 });         // method 0
            bomb.AddRange(new byte[4]);                 // 时间/日期
            bomb.AddRange(new byte[4]);                 // crc
            bomb.AddRange(Bytes.U32LE(0));              // csize = 0（未知）
            bomb.AddRange(Bytes.U32LE(0));              // usize
            bomb.AddRange(new byte[] { 19, 0 });        // fnlen = "docProps/custom.xml"
            bomb.AddRange(new byte[] { 0, 0 });         // extra
            bomb.AddRange(Encoding.ASCII.GetBytes("docProps/custom.xml"));
            bomb.AddRange(new byte[5 * 1024 * 1024]);   // 无 PK 签名的尾

            var result = AigcLabelVerifier.Verify(bomb.ToArray());

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
            Assert.Contains(result.Signals, s => s.Kind == SignalKind.ResourceLimitExceeded);
        }

        [Fact]
        public void Ooxml_BogusCentralDirectoryOffset_FallsBack_NoEscape_Inconclusive()
        {
            // EOCD 声明的 CD 偏移越过文件 → 中央目录路径放弃 → 回退本地头遍历；
            // 负载为垃圾字节 → 负载畸形错误 → 无法判定
            var bomb = new List<byte>();
            bomb.AddRange(OoxmlBuilder.BuildRaw(new byte[] { 0x00, 0xFF, 0x10 }, method: 0));
            bomb.AddRange(new byte[] { 0x50, 0x4B, 0x05, 0x06 }); // EOCD
            bomb.AddRange(new byte[4]);                 // disk 字段 ×2
            bomb.AddRange(new byte[] { 1, 0 });         // entries this disk
            bomb.AddRange(new byte[] { 1, 0 });         // total entries
            bomb.AddRange(Bytes.U32LE(46));             // cd size
            bomb.AddRange(Bytes.U32LE(0x7FFFFFF0u));    // cd offset 越界
            bomb.AddRange(new byte[] { 0, 0 });         // comment len

            var result = AigcLabelVerifier.Verify(bomb.ToArray());

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        [Fact]
        public void Ooxml_CorruptCentralDirectory_FallsBack_NoEscape_Inconclusive()
        {
            // EOCD 宣称 65534 条目但"CD"区域只有 4 字节垃圾 → 首条目签名不符 → 回退本地头遍历
            var bomb = new List<byte>();
            bomb.AddRange(OoxmlBuilder.BuildRaw(new byte[] { 0x01 }, method: 0));
            uint cdStart = (uint)bomb.Count;
            bomb.AddRange(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }); // 非 CD 条目签名
            bomb.AddRange(new byte[] { 0x50, 0x4B, 0x05, 0x06 }); // EOCD
            bomb.AddRange(new byte[4]);                 // disk 字段 ×2
            bomb.AddRange(new byte[] { 0xFE, 0xFF });   // entries = 65534（炸弹值）
            bomb.AddRange(new byte[] { 0xFE, 0xFF });   // total entries
            bomb.AddRange(Bytes.U32LE(4));              // cd size
            bomb.AddRange(Bytes.U32LE(cdStart));        // cd offset
            bomb.AddRange(new byte[] { 0, 0 });         // comment len

            var result = AigcLabelVerifier.Verify(bomb.ToArray());

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
        }

        [Fact]
        public void Ooxml_CdLocalOffsetMismatch_FallsBackToLocals()
        {
            // CD 命中目标条目但本地头偏移处无 PK\x03\x04 → CD 与布局矛盾 → 回退本地头遍历，
            // 真实条目经回退路径正常提取 → 合规（回退救援语义的回归锚点）
            const string validJson = "{\"Label\":\"1\",\"ContentProducer\":\"RobustStudio\",\"ProduceID\":\"R-9\"," +
                "\"ReservedCode1\":\"\",\"ContentPropagator\":\"\",\"PropagateID\":\"\",\"ReservedCode2\":\"\"}";
            var bomb = new List<byte>();
            bomb.AddRange(OoxmlBuilder.Build(validJson));
            uint cdStart = (uint)bomb.Count;
            bomb.AddRange(CdEntry("docProps/custom.xml", localOffset: cdStart)); // 偏移指向 EOCD 自身
            bomb.AddRange(new byte[] { 0x50, 0x4B, 0x05, 0x06 }); // EOCD
            bomb.AddRange(new byte[4]);                 // disk 字段 ×2
            bomb.AddRange(new byte[] { 1, 0 });         // entries this disk
            bomb.AddRange(new byte[] { 1, 0 });         // total entries
            bomb.AddRange(Bytes.U32LE((uint)(bomb.Count - cdStart))); // cd size
            bomb.AddRange(Bytes.U32LE(cdStart));        // cd offset
            bomb.AddRange(new byte[] { 0, 0 });         // comment len

            var result = AigcLabelVerifier.Verify(bomb.ToArray());

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.OoxmlCustomAigc && c.Outcome == CheckOutcome.Pass);
        }

        /// <summary>46 字节固定头 + 名字的中央目录条目（偏移/长度炸弹用例的构造器）。</summary>
        private static byte[] CdEntry(string name, uint localOffset)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            var b = new List<byte>();
            b.AddRange(new byte[] { 0x50, 0x4B, 0x01, 0x02 }); // 签名
            b.AddRange(new byte[] { 20, 0 });                  // version made by
            b.AddRange(new byte[] { 20, 0 });                  // version needed
            b.AddRange(new byte[2]);                           // flags
            b.AddRange(new byte[2]);                           // method 0
            b.AddRange(new byte[4]);                           // 时间/日期
            b.AddRange(new byte[4]);                           // crc
            b.AddRange(Bytes.U32LE(0));                        // csize
            b.AddRange(Bytes.U32LE(0));                        // usize
            b.AddRange(new byte[] { (byte)nameBytes.Length, 0 }); // fnlen
            b.AddRange(new byte[2]);                           // extra
            b.AddRange(new byte[2]);                           // comment
            b.AddRange(new byte[2]);                           // disk start
            b.AddRange(new byte[2]);                           // internal attrs
            b.AddRange(new byte[4]);                           // external attrs
            b.AddRange(Bytes.U32LE(localOffset));
            b.AddRange(nameBytes);
            return b.ToArray();
        }

        // —— PDF / 文本 ——

        [Fact]
        public void Pdf_LiteralStringBombs_NeverEscape()
        {
            var inputs = new List<byte[]>
            {
                // 未终止字面串 + 百万层括号嵌套：迭代深度计数，无递归，EOF 终止
                Encoding.ASCII.GetBytes("%PDF-1.7\n/AIGC " + new string('(', 1_000_000)),
                // 转义符悬在 EOF
                Encoding.ASCII.GetBytes("%PDF-1.7\n/AIGC (\\"),
                // 八进制转义越界值 \777 → 511 截断为字节
                Encoding.ASCII.GetBytes("%PDF-1.7\n/AIGC (\\777)"),
            };

            foreach (byte[] input in inputs)
            {
                VerificationResult result = AigcLabelVerifier.Verify(input);
                Assert.InRange((int)result.Verdict, 1, 4);
                Assert.NotEmpty(result.Checks);
            }
        }

        [Fact]
        public void Text_FrontMatterBombs_NeverEscape()
        {
            var inputs = new List<string>
            {
                "---\nAIGC:\n  no-colon-here\n---\n正文",       // 缩进块无冒号 → 负载畸形
                "---\nAIGC:\n" + string.Join("\n", Enumerable.Repeat("  key: 'v'", 1000)) + "\n", // 超限块 → 扫描上限截断
                "---\nAIGC:",                                    // EOF 悬在 AIGC 行
                "---\nAIGC:\n\tvalue\n",                         // 制表缩进无冒号
            };

            foreach (string input in inputs)
            {
                VerificationResult result = AigcLabelVerifier.Verify(Encoding.UTF8.GetBytes(input));
                Assert.InRange((int)result.Verdict, 1, 4);
                Assert.NotEmpty(result.Checks);
            }
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
                // 载体扩张后的 8 类 + Markdown：随机变异必须覆盖全部 14 类解析器
                FlacBuilder.Build(validJson),
                OggBuilder.Build(validJson),
                AviBuilder.Build(validJson),
                WebpBuilder.Build(validXmp),
                TiffBuilder.Build(validXmp),
                GifBuilder.Build(validXmp),
                OoxmlBuilder.Build(validJson),
                OoxmlBuilder.BuildDeflate(validJson),
                PdfBuilder.Build(validJson),
                System.Text.Encoding.UTF8.GetBytes(MdBuilder.FrontMatter("1", "RobustStudio", "R-9")),
            };
        }
    }
}
