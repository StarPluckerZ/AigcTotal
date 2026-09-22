using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AigcTotal.GB45438.Carriers.Parsing;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Carriers.Parsing.Gif
{
    /// <summary>
    /// GIF 解析（TC260-PG-20259A）：Application Extension（0x21 0xFF），应用标识符 "XMP Data"，
    /// 数据子块重组为 XMP 包（走 XMP 解码器）。遍历块结构：图像描述符/其他扩展按规范跳过。
    /// </summary>
    public sealed class GifParser : ICarrierParser
    {
        public CarrierKind Kind => CarrierKind.Gif;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            reader.Seek(0);
            reader.ReadExactly(6, "GIF signature");   // GIF87a / GIF89a
            reader.ReadExactly(7, "logical screen descriptor"); // 全局色表由 packed 位决定，但首扩展/图像块前的色表读取可省——见下注
            // 注：若 packed 位声明全局色表，需跳过后才能读块；packed 在第 10 字节
            SkipGlobalColorTable(reader);

            var sites = new List<LabelSite>();
            var signals = new List<ForensicSignal>();
            var checks = new List<CheckResult>();
            bool sawXmp = false;

            while (reader.Position < reader.Length)
            {
                ct.ThrowIfCancellationRequested();
                reader.CountStructure();

                int introducer = ReadByteOrThrow(reader, signals, "block introducer");
                if (introducer == 0x3B) break; // trailer

                if (introducer == 0x21)
                {
                    int label = ReadByteOrThrow(reader, signals, "extension label");
                    if (label == 0xFF && TryReadXmpAppExtension(reader, sites, signals, ref sawXmp))
                    {
                        // XMP 站点已登记
                    }
                    else
                    {
                        SkipSubBlocks(reader, signals);
                    }
                }
                else if (introducer == 0x2C)
                {
                    SkipImageDescriptor(reader, signals);
                }
                else
                {
                    signals.Add(new ForensicSignal(SignalKind.StructureMalformed, null,
                        $"unknown block introducer 0x{introducer:X2} at offset {reader.Position - 1}"));
                    break;
                }
            }

            checks.Add(sawXmp
                ? new CheckResult(CheckIds.GifAppExtAigc, CheckOutcome.Pass)
                : new CheckResult(CheckIds.GifAppExtAigc, CheckOutcome.Skip));
            return new CarrierScan(sites, signals, checks);
        }

        private static void SkipGlobalColorTable(BoundedReader reader)
        {
            // 逻辑屏幕描述符：宽(2) 高(2) packed(1) 背景(1) 长宽比(1)——packed bit7=全局色表存在
            long packedPos = 6 + 4; // magic 6 + 宽高 4
            reader.Seek(packedPos);
            byte packed = reader.ReadAtMost(1)[0];
            reader.Seek(packedPos + 3); // 回到描述符尾
            if ((packed & 0x80) != 0)
            {
                int tableEntries = 2 << (packed & 0x07);
                reader.Seek(reader.Position + tableEntries * 3L);
            }
        }

        /// <summary>0x21 0xFF：块长(=11) + 应用标识符(8) + 鉴别码(3) + 数据子块。</summary>
        private static bool TryReadXmpAppExtension(BoundedReader reader,
            List<LabelSite> sites, List<ForensicSignal> signals, ref bool sawXmp)
        {
            long extOffset = reader.Position - 2;
            byte blockSize = reader.ReadAtMost(1)[0];
            byte[] ident = reader.ReadExactly(blockSize, "app identifier");
            // Adobe XMP 规范：标识符 "XMP Data"；鉴别码不校验
            if (blockSize != 11 || ident[0] != (byte)'X' || ident[1] != (byte)'M' || ident[2] != (byte)'P')
            {
                // 非目标应用扩展：回退重读（子块按普通扩展跳过）
                reader.Seek(extOffset + 2);
                SkipSubBlocks(reader, signals);
                return false;
            }

            var packet = new List<byte>();
            while (true)
            {
                byte len = reader.ReadAtMost(1)[0];
                if (len == 0) break;
                packet.AddRange(reader.ReadExactly(len, "XMP sub-block"));
            }

            if (packet.Count == 0)
            {
                signals.Add(new ForensicSignal(SignalKind.MetadataShellEmpty,
                    new SiteLocation(new List<object> { "AppExt", 0 }, extOffset, 0),
                    "XMP Data application extension with empty packet"));
                return false;
            }
            sites.Add(new LabelSite(
                new SiteLocation(new List<object> { "AppExt", 0 }, extOffset, packet.Count),
                PayloadEncoding.XmpAigc,
                packet.ToArray()));
            sawXmp = true;
            return true;
        }

        /// <summary>图像描述符：左(2)上(2)宽(2)高(2)packed(1)[局部色表]LZW码长(1)+子块。</summary>
        private static void SkipImageDescriptor(BoundedReader reader, List<ForensicSignal> signals)
        {
            byte[] desc = reader.ReadExactly(9, "image descriptor");
            if ((desc[8] & 0x80) != 0)
            {
                int tableEntries = 2 << (desc[8] & 0x07);
                reader.Seek(reader.Position + tableEntries * 3L);
            }
            reader.ReadAtMost(1); // LZW 最小编码长度
            SkipSubBlocks(reader, signals);
        }

        /// <summary>读子块序列（len 字节 + 数据），直到 0 终止符。</summary>
        private static void SkipSubBlocks(BoundedReader reader, List<ForensicSignal> signals)
        {
            while (true)
            {
                byte len = reader.ReadAtMost(1)[0];
                if (len == 0) return;
                reader.Seek(reader.Position + len);
            }
        }

        private static int ReadByteOrThrow(BoundedReader reader, List<ForensicSignal> signals, string ctx)
        {
            byte[] one = reader.ReadAtMost(1);
            if (one.Length == 0)
            {
                signals.Add(new ForensicSignal(SignalKind.StructureTruncated, null,
                    $"unexpected EOF while reading {ctx}"));
                return 0x3B; // 视为终止
            }
            return one[0];
        }
    }
}
