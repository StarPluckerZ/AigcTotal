using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AigcTotal.GB45438.Carriers.Parsing;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Carriers.Parsing.Mp4
{
    /// <summary>
    /// MP4/MOV（QuickTime 家族）解析：递归遍历 box 树，两条标识通道——
    /// ① uuid box（XMP UUID be7acfcb-…）携带的 XMP 负载；② udta 下 aigc/AIGC box 的 JSON 负载。
    /// mdat 等大块直接 Seek 跳过（不读字节）；box 深度由 BoundedReader.EnterScope 封顶。
    /// udta 通道的 atom 名为推测实现，待 TC260 音视频指南原文校准。
    /// </summary>
    public sealed class Mp4Parser : ICarrierParser
    {
        // Adobe XMP 的 uuid box 标识：BE7ACFCB-97A9-42E8-9C71-999491E3AFAC
        private static readonly byte[] XmpUuid =
        {
            0xBE, 0x7A, 0xCF, 0xCB, 0x97, 0xA9, 0x42, 0xE8,
            0x9C, 0x71, 0x99, 0x94, 0x91, 0xE3, 0xAF, 0xAC,
        };

        private static readonly HashSet<string> ContainerTypes = new HashSet<string>
        {
            "moov", "trak", "mdia", "minf", "stbl", "udta", "meta", "ilst", "moof", "traf", "mvex",
        };

        public CarrierKind Kind => CarrierKind.Mp4;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            reader.Seek(0);
            var state = new ScanState();
            Walk(reader, reader.Length, insideUdta: false, state, ct);

            var checks = new List<CheckResult>
            {
                state.SawXmp
                    ? new CheckResult(CheckIds.Mp4XmpAigc, CheckOutcome.Pass)
                    : new CheckResult(CheckIds.Mp4XmpAigc, CheckOutcome.Skip),
                state.SawUdtaAigc
                    ? new CheckResult(CheckIds.Mp4UdtaAigc, CheckOutcome.Pass)
                    : new CheckResult(CheckIds.Mp4UdtaAigc, CheckOutcome.Skip),
            };
            return new CarrierScan(state.Sites, state.Signals, checks, state.Brand);
        }

        private sealed class ScanState
        {
            public List<LabelSite> Sites { get; } = new List<LabelSite>();
            public List<ForensicSignal> Signals { get; } = new List<ForensicSignal>();
            public string? Brand { get; set; }
            public bool SawXmp { get; set; }
            public bool SawUdtaAigc { get; set; }
            public int UuidIndex { get; set; }
            public int AigcIndex { get; set; }
        }

        private static void Walk(BoundedReader reader, long end, bool insideUdta, ScanState state, CancellationToken ct)
        {
            while (reader.Position < end)
            {
                ct.ThrowIfCancellationRequested();
                reader.CountStructure();

                long boxOffset = reader.Position;
                long available = end - boxOffset;
                if (available < 8)
                {
                    state.Signals.Add(new ForensicSignal(SignalKind.StructureMalformed, null,
                        $"{available} trailing bytes inside box region at offset {boxOffset}"));
                    break;
                }

                uint size32 = reader.ReadUInt32BE("box size");
                byte[] typeBytes = reader.ReadExactly(4, "box type");
                string type = Encoding.ASCII.GetString(typeBytes);

                long headerSize = 8;
                ulong size = size32;
                if (size32 == 1)
                {
                    size = reader.ReadUInt64BE("box largesize");
                    headerSize = 16;
                }
                else if (size32 == 0)
                {
                    size = (ulong)available; // 到父级末尾
                }

                if (size < (ulong)headerSize)
                {
                    state.Signals.Add(new ForensicSignal(SignalKind.StructureMalformed, null,
                        $"box {type} declares size {size} < header {headerSize} at offset {boxOffset}"));
                    break;
                }

                long dataStart = reader.Position;
                if ((ulong)available < size)
                {
                    state.Signals.Add(new ForensicSignal(SignalKind.StructureTruncated, null,
                        $"box {type} at offset {boxOffset} declares {size} bytes but only {available} available"));
                    break;
                }
                long dataSize = (long)(size - (ulong)headerSize);
                long dataEnd = dataStart + dataSize;

                if (ContainerTypes.Contains(type))
                {
                    long childStart = dataStart;
                    if (type == "meta")
                    {
                        // meta 是 full box：子 box 前有 4 字节 version/flags
                        if (dataSize < 4)
                        {
                            state.Signals.Add(new ForensicSignal(SignalKind.StructureMalformed, null,
                                $"meta box at offset {boxOffset} too small for full-box header"));
                            reader.Seek(dataEnd);
                            continue;
                        }
                        childStart += 4;
                    }
                    reader.Seek(childStart);
                    try
                    {
                        reader.EnterScope();
                        Walk(reader, dataEnd, insideUdta || type == "udta", state, ct);
                    }
                    finally
                    {
                        reader.ExitScope();
                    }
                    reader.Seek(dataEnd);
                }
                else if (type == "uuid" && dataSize >= 16)
                {
                    byte[] usertype = reader.ReadExactly(16, "uuid usertype");
                    if (BytesEqual(usertype, XmpUuid))
                    {
                        byte[] payload = reader.ReadExactly(dataSize - 16, "uuid XMP payload");
                        state.Sites.Add(new LabelSite(
                            new SiteLocation(new List<object> { "uuid", state.UuidIndex++ },
                                dataStart + 16, payload.Length),
                            PayloadEncoding.XmpAigc,
                            payload));
                        state.SawXmp = true;
                    }
                    reader.Seek(dataEnd);
                }
                else if (type == "ftyp" && dataSize >= 4)
                {
                    byte[] brand = reader.ReadExactly(4, "major brand");
                    state.Brand = Encoding.ASCII.GetString(brand);
                    reader.Seek(dataEnd);
                }
                else if (type == "aigc" || type == "AIGC")
                {
                    byte[] payload = reader.ReadExactly(dataSize, "aigc payload");
                    state.Sites.Add(new LabelSite(
                        new SiteLocation(new List<object> { type, state.AigcIndex++ }, dataStart, payload.Length),
                        PayloadEncoding.Json,
                        payload));
                    if (insideUdta)
                    {
                        state.SawUdtaAigc = true;
                    }
                    reader.Seek(dataEnd);
                }
                else
                {
                    // mdat 等大块：跳过，不读字节
                    reader.Seek(dataEnd);
                }
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }
    }
}
