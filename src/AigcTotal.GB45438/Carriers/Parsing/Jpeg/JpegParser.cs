using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AigcTotal.GB45438.Carriers.Parsing;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Carriers.Parsing.Jpeg
{
    /// <summary>
    /// JPEG 容器解析：遍历 SOS 之前的 marker 段，枚举 APP1 站点，检测截断。两条通道：
    /// - XMP：APP1 标识符 "http://ns.adobe.com/xap/1.0/\0"；
    /// - EXIF UserComment：APP1 标识符 "Exif\0\0" → TIFF IFD0 tag 0x9286（type 7 UNDEFINED），
    ///   值 = 8 字节字符码（"ASCII\0\0\0"）+ 负载（TC260-PG-20259A 附录 B 的 {"AIGC":{…}} 包裹形态）。
    ///   UserComment 是通用备注字段，普通照片满地都是——只有负载形如 {"AIGC"… 或 {"Label"…
    ///   （指南包裹形态 / 生态裸形态）才建站点，其余静默忽略，避免把普通照片拖进误判。
    /// </summary>
    public sealed class JpegParser : ICarrierParser
    {
        // 标准 XMP APP1 标识符（含结尾 NUL）
        private static readonly byte[] XmpIdentifier =
            Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");

        private static readonly byte[] ExifIdentifier = Encoding.ASCII.GetBytes("Exif\0\0");
        private const ushort TagUserComment = 0x9286;

        public CarrierKind Kind => CarrierKind.Jpeg;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            reader.Seek(0);
            reader.ReadExactly(2, "SOI"); // FF D8，魔数已由探测器确认

            var sites = new List<LabelSite>();
            var signals = new List<ForensicSignal>();
            var checks = new List<CheckResult>();
            int app1Index = 0;
            bool sawXmpSite = false;
            bool sawExifSite = false;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                reader.CountStructure();

                // 前导填充字节：允许连续 0xFF，第一个非 FF 字节为 marker
                int b = ReadByteOrThrow(reader, "marker prefix");
                while (b == 0xFF)
                {
                    b = ReadByteOrThrow(reader, "marker");
                }
                int marker = b;

                if (marker == 0xD9) break;            // EOI
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue; // 独立 marker，无长度
                if (marker == 0xDA) break;            // SOS：熵编码数据开始，元数据段结束

                long segmentOffset = reader.Position - 2;
                uint length = reader.ReadUInt16BE("segment length");
                if (length < 2)
                {
                    signals.Add(new ForensicSignal(SignalKind.StructureMalformed,
                        new SiteLocation(new List<object> { "marker", marker }, segmentOffset, 4),
                        $"segment length {length} < 2 at offset {segmentOffset}"));
                    break;
                }

                byte[] payload = reader.ReadExactly(length - 2, $"segment 0xFF{marker:X2}");

                if (marker == 0xE1) // APP1
                {
                    int current = app1Index;
                    app1Index++;
                    if (StartsWith(payload, XmpIdentifier))
                    {
                        var xmp = new byte[payload.Length - XmpIdentifier.Length];
                        Array.Copy(payload, XmpIdentifier.Length, xmp, 0, xmp.Length);
                        if (xmp.Length == 0)
                        {
                            signals.Add(new ForensicSignal(SignalKind.MetadataShellEmpty,
                                new SiteLocation(new List<object> { "APP1", current }, segmentOffset, length + 2),
                                "XMP APP1 with empty packet"));
                        }
                        else
                        {
                            sites.Add(new LabelSite(
                                new SiteLocation(new List<object> { "APP1", current },
                                    segmentOffset + 4 + XmpIdentifier.Length, xmp.Length),
                                PayloadEncoding.XmpAigc,
                                xmp));
                            sawXmpSite = true;
                        }
                    }
                    else if (StartsWith(payload, ExifIdentifier))
                    {
                        CollectExifUserComment(payload, segmentOffset + 4, current, sites, signals, ref sawExifSite);
                    }
                }
            }

            checks.Add(sawXmpSite
                ? new CheckResult(CheckIds.JpegApp1Xmp, CheckOutcome.Pass)
                : new CheckResult(CheckIds.JpegApp1Xmp, CheckOutcome.Skip));
            checks.Add(sawExifSite
                ? new CheckResult(CheckIds.JpegExifUserComment, CheckOutcome.Pass)
                : new CheckResult(CheckIds.JpegExifUserComment, CheckOutcome.Skip));

            return new CarrierScan(sites, signals, checks);
        }

        /// <summary>
        /// 在内存中的 Exif APP1 负载里定位 TIFF IFD0 的 UserComment（tag 0x9286）。
        /// TIFF 结构损坏 → 按普通照片静默忽略（EXIF 是辅助元数据，非指南强制形态；
        /// 只有 tag 命中但值不可读时才出畸形信号——那才是“通道在、读不出”的防假性场景）。
        /// </summary>
        private static void CollectExifUserComment(byte[] payload, long payloadOffset, int app1Index,
            List<LabelSite> sites, List<ForensicSignal> signals, ref bool sawExifSite)
        {
            int tiffStart = ExifIdentifier.Length; // "Exif\0\0" 之后为 TIFF 头
            if (payload.Length < tiffStart + 8) return;

            bool littleEndian = payload[tiffStart] == (byte)'I' && payload[tiffStart + 1] == (byte)'I';
            if (!littleEndian && !(payload[tiffStart] == (byte)'M' && payload[tiffStart + 1] == (byte)'M')) return;

            long ifdOffset = ReadU32(payload, tiffStart + 4, littleEndian);
            if (ifdOffset <= 0 || tiffStart + ifdOffset + 2 > payload.Length) return;

            int ifdPos = tiffStart + (int)ifdOffset;
            int entryCount = ReadU16(payload, ifdPos, littleEndian);
            if (entryCount > 10_000) return;

            for (int i = 0; i < entryCount; i++)
            {
                int entryPos = ifdPos + 2 + i * 12;
                if (entryPos + 12 > payload.Length) return; // IFD 越界：静默忽略（非 AIGC 通道故障）
                ushort tag = (ushort)ReadU16(payload, entryPos, littleEndian);
                ushort type = (ushort)ReadU16(payload, entryPos + 2, littleEndian);
                uint count = ReadU32(payload, entryPos + 4, littleEndian);

                if (tag != TagUserComment) continue;
                long total = (long)TypeByteSize(type) * count;
                if (total <= 0)
                {
                    signals.Add(new ForensicSignal(SignalKind.StructureMalformed,
                        new SiteLocation(new List<object> { "APP1", app1Index, "Exif", "UserComment" },
                            payloadOffset, payload.Length),
                        $"UserComment entry has unsupported type {type}"));
                    return;
                }

                byte[] value;
                if (total <= 4)
                {
                    value = new byte[total];
                    Array.Copy(payload, entryPos + 8, value, 0, total);
                }
                else
                {
                    long valueOffset = ReadU32(payload, entryPos + 8, littleEndian);
                    if (tiffStart + valueOffset + total > payload.Length)
                    {
                        signals.Add(new ForensicSignal(SignalKind.StructureMalformed,
                            new SiteLocation(new List<object> { "APP1", app1Index, "Exif", "UserComment" },
                                payloadOffset, payload.Length),
                            $"UserComment value at TIFF offset {valueOffset} beyond APP1"));
                        return;
                    }
                    value = new byte[total];
                    Array.Copy(payload, tiffStart + valueOffset, value, 0, total);
                }

                // 值 = 8 字节字符码（TIFF 6.0：'ASCII\0\0\0' 等）+ 负载；只认 ASCII 码，
                // 其余字符码（UNICODE 等）忽略——指南形态与生态实现均以 ASCII 承载 JSON
                if (value.Length <= 8 || !IsAsciiCharCode(value)) return;

                var content = new byte[value.Length - 8];
                Array.Copy(value, 8, content, 0, content.Length);
                if (!LooksLikeLabelPayload(content)) return; // 普通备注文本：忽略

                // 站点文件内绝对偏移：内联值在条目 value/offset 字段处，外置值在 TIFF 头 + 偏移 + 8（字符码之后）
                long siteOffset = total <= 4
                    ? payloadOffset + entryPos + 8
                    : payloadOffset + tiffStart + ReadU32(payload, entryPos + 8, littleEndian) + 8;
                sites.Add(new LabelSite(
                    new SiteLocation(new List<object> { "APP1", app1Index, "Exif", "UserComment" },
                        siteOffset, content.Length),
                    PayloadEncoding.Json,
                    content));
                sawExifSite = true;
                return;
            }
        }

        /// <summary>TIFF UserComment 字符码区固定 8 字节；识别 'ASCII' + 3 个 NUL。</summary>
        private static bool IsAsciiCharCode(byte[] value) =>
            value[0] == (byte)'A' && value[1] == (byte)'S' && value[2] == (byte)'C'
            && value[3] == (byte)'I' && value[4] == (byte)'I'
            && value[5] == 0 && value[6] == 0 && value[7] == 0;

        /// <summary>
        /// 通用备注字段防误判：只有指南包裹形态（"{"AIGC"…）或生态裸形态（"{"Label"…）开头的
        /// 内容才视为可能的标识负载（允许前导空白）；其余静默忽略。
        /// </summary>
        internal static bool LooksLikeLabelPayload(byte[] content)
        {
            int start = 0;
            while (start < content.Length && (content[start] == (byte)' ' || content[start] == (byte)'\t')) start++;
            if (content.Length - start < 8) return false;
            return content[start] == (byte)'{'
                && content[start + 1] == (byte)'"'
                && (StartsWithAt(content, start + 2, AigcKeyBytes) || StartsWithAt(content, start + 2, LabelKeyBytes));
        }

        private static readonly byte[] AigcKeyBytes = Encoding.ASCII.GetBytes("AIGC\"");
        private static readonly byte[] LabelKeyBytes = Encoding.ASCII.GetBytes("Label\"");

        private static bool StartsWithAt(byte[] data, int pos, byte[] prefix)
        {
            if (data.Length - pos < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
            {
                if (data[pos + i] != prefix[i]) return false;
            }
            return true;
        }

        private static long TypeByteSize(ushort type)
        {
            switch (type)
            {
                case 1: case 2: case 6: case 7: return 1;   // BYTE/ASCII/SBYTE/UNDEFINED
                case 3: case 8: return 2;                   // SHORT
                case 4: case 9: case 11: return 4;          // LONG/SLONG/FLOAT
                case 5: case 10: case 12: return 8;         // RATIONAL/SRATIONAL/DOUBLE
                default: return 0;                           // 未知类型 → 由调用方判畸形
            }
        }

        private static int ReadU16(byte[] data, int pos, bool le) =>
            le ? data[pos] | (data[pos + 1] << 8) : (data[pos] << 8) | data[pos + 1];

        private static uint ReadU32(byte[] data, int pos, bool le) =>
            le
                ? (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24))
                : (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);

        private static int ReadByteOrThrow(BoundedReader reader, string context)
        {
            byte[] one = reader.ReadAtMost(1);
            if (one.Length == 0)
            {
                throw new CarrierStructureException(CheckCodes.StructureTruncated,
                    $"unexpected EOF while reading {context}");
            }
            return one[0];
        }

        private static bool StartsWith(byte[] payload, byte[] prefix)
        {
            if (payload.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
            {
                if (payload[i] != prefix[i]) return false;
            }
            return true;
        }
    }
}
