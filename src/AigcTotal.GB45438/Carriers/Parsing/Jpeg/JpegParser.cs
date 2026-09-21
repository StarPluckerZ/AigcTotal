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
    /// JPEG 容器解析：遍历 SOS 之前的 marker 段，枚举 APP1(XMP) 站点，检测截断。
    /// </summary>
    public sealed class JpegParser : ICarrierParser
    {
        // 标准 XMP APP1 标识符（含结尾 NUL）
        private static readonly byte[] XmpIdentifier =
            Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");

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
                }
            }

            checks.Add(sawXmpSite
                ? new CheckResult(CheckIds.JpegApp1Xmp, CheckOutcome.Pass)
                : new CheckResult(CheckIds.JpegApp1Xmp, CheckOutcome.Skip));

            return new CarrierScan(sites, signals, checks);
        }

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
