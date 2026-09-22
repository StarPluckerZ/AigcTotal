using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AigcTotal.GB45438.Carriers.Parsing;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Carriers.Parsing.Tiff
{
    /// <summary>
    /// TIFF 解析（TC260-PG-20259A）：IFD0 中 tag 0x2BC（XMP）、Field type 7（UNDEFINED）；
    /// 负载为 XMP 包（≤4 字节内联于条目，否则为偏移）。II/MM 字节序均支持。
    /// </summary>
    public sealed class TiffParser : ICarrierParser
    {
        private const ushort TagXmp = 0x02BC;

        public CarrierKind Kind => CarrierKind.Tiff;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            reader.Seek(0);
            byte[] order = reader.ReadExactly(2, "byte order");
            bool littleEndian = order[0] == (byte)'I' && order[1] == (byte)'I';
            reader.ReadExactly(2, "magic 42");
            long ifdOffset = ReadU32(reader, littleEndian, "IFD0 offset");

            var sites = new List<LabelSite>();
            var signals = new List<ForensicSignal>();
            var checks = new List<CheckResult>();
            bool sawXmp = false;

            if (ifdOffset > 0 && ifdOffset < reader.Length)
            {
                reader.Seek(ifdOffset);
                ushort entryCount = ReadU16(reader, littleEndian, "IFD entry count");
                if (entryCount > 10_000)
                {
                    signals.Add(new ForensicSignal(SignalKind.StructureMalformed, null,
                        $"IFD0 entry count {entryCount} unreasonable"));
                    entryCount = 0;
                }

                for (ushort i = 0; i < entryCount && !sawXmp; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    reader.CountStructure();
                    ushort tag = ReadU16(reader, littleEndian, "entry tag");
                    ushort type = ReadU16(reader, littleEndian, "entry type");
                    uint count = ReadU32(reader, littleEndian, "entry count");
                    long valueFieldOffset = reader.Position;
                    reader.Seek(reader.Position + 4); // value/offset 字段

                    if (tag != TagXmp) continue;

                    long total = TypeByteSize(type) * count;
                    if (total <= 0)
                    {
                        signals.Add(new ForensicSignal(SignalKind.StructureMalformed, null,
                            $"XMP entry type {type} has unsupported unit size"));
                        break;
                    }
                    byte[] payload;
                    if (total <= 4)
                    {
                        long here = reader.Position;
                        reader.Seek(valueFieldOffset);
                        payload = reader.ReadExactly(total, "XMP inline value");
                        reader.Seek(here);
                    }
                    else
                    {
                        reader.Seek(valueFieldOffset); // value/offset 字段位于条目内
                        long valueOffset = ReadU32(reader, littleEndian, "XMP value offset");
                        if (valueOffset >= reader.Length || valueOffset + total > reader.Length)
                        {
                            signals.Add(new ForensicSignal(SignalKind.StructureTruncated, null,
                                $"XMP value at offset {valueOffset} + {total} beyond file"));
                            break;
                        }
                        reader.Seek(valueOffset);
                        payload = reader.ReadExactly(total, "XMP value");
                    }
                    sites.Add(new LabelSite(
                        new SiteLocation(new List<object> { "IFD0", 0 }, ifdOffset + 2 + (long)i * 12, total),
                        PayloadEncoding.XmpAigc,
                        payload));
                    sawXmp = true;
                }
            }

            checks.Add(sawXmp
                ? new CheckResult(CheckIds.TiffIfd0Aigc, CheckOutcome.Pass)
                : new CheckResult(CheckIds.TiffIfd0Aigc, CheckOutcome.Skip));
            return new CarrierScan(sites, signals, checks);
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

        private static ushort ReadU16(BoundedReader reader, bool le, string ctx)
        {
            byte[] b = reader.ReadExactly(2, ctx);
            return le ? (ushort)(b[0] | (b[1] << 8)) : (ushort)((b[0] << 8) | b[1]);
        }

        private static uint ReadU32(BoundedReader reader, bool le, string ctx)
        {
            byte[] b = reader.ReadExactly(4, ctx);
            return le
                ? b[0] | ((uint)b[1] << 8) | ((uint)b[2] << 16) | ((uint)b[3] << 24)
                : ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }
    }
}
