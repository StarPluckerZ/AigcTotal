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
    /// MP4/MOV（QuickTime 家族）解析：递归遍历 box 树，三条标识通道——
    /// ① moov.udta.meta.keys（key=AIGC）+ moov.udta.meta.ilst（value=裸 JSON）：
    ///    TC260-PG-20257A 规定形态（ffmpeg -movflags use_metadata_tags 实测一致）；
    /// ② uuid box（XMP UUID be7acfcb-…）携带的 XMP 负载（真实样本实测，非指南条款）；
    /// ③ udta 下字面 aigc/AIGC box 的 JSON 负载（非标实现的兼容探测）。
    /// mdat 等大块直接 Seek 跳过（不读字节）；box 深度由 BoundedReader.EnterScope 封顶。
    /// </summary>
    public sealed class Mp4Parser : ICarrierParser
    {
        // Adobe XMP 的 uuid box 标识：BE7ACFCB-97A9-42E8-9C71-999491E3AFAC
        private static readonly byte[] XmpUuid =
        {
            0xBE, 0x7A, 0xCF, 0xCB, 0x97, 0xA9, 0x42, 0xE8,
            0x9C, 0x71, 0x99, 0x94, 0x91, 0xE3, 0xAF, 0xAC,
        };

        // 注意 ilst 不在此列：它有专用解析（keys 序号 → 键名映射 + data 负载提取），
        // 走通用容器递归会因条目类型是二进制序号而全部漏检
        private static readonly HashSet<string> ContainerTypes = new HashSet<string>
        {
            "moov", "trak", "mdia", "minf", "stbl", "udta", "meta", "moof", "traf", "mvex",
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

            /// <summary>QuickTime 元数据键空间：keys 框的序号 → 键名（TC260-PG-20257A 规定形态）。</summary>
            public Dictionary<uint, string> MetaKeys { get; } = new Dictionary<uint, string>();

            public int IlstIndex { get; set; }
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
                else if (type == "keys" && insideUdta)
                {
                    // TC260-PG-20257A：moov.udta.meta.keys 写 key：AIGC（QuickTime 元数据键空间）
                    ParseKeys(reader, dataSize, state);
                    reader.Seek(dataEnd);
                }
                else if (type == "ilst" && insideUdta)
                {
                    // TC260-PG-20257A：moov.udta.meta.ilst 写 value：裸 JSON
                    ParseIlst(reader, dataSize, state);
                    reader.Seek(dataEnd);
                }
                else if (type == "aigc" || type == "AIGC")
                {
                    // 兼容探测：字面 aigc box 非指南规定形态，保留以防非标实现
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

        /// <summary>
        /// keys 框（full box）：version/flags(4) + entry_count(u32) + N × { key_size(u32)，
        /// key_namespace(u32，实测 'mdta')，key_name(以 NUL 结尾) }。建立 序号→键名 映射。
        /// </summary>
        private static void ParseKeys(BoundedReader reader, long dataSize, ScanState state)
        {
            if (dataSize < 8) return;
            reader.ReadUInt32BE("keys version/flags");
            uint count = reader.ReadUInt32BE("keys entry count");
            if (count > 10_000)
            {
                state.Signals.Add(new ForensicSignal(SignalKind.StructureMalformed, null,
                    $"keys entry count {count} unreasonable"));
                return;
            }
            for (uint i = 0; i < count; i++)
            {
                uint keySize = reader.ReadUInt32BE("key size");
                // keySize 来自不可信文件：(int)keySize 在 ≥2^31 时回绕为负——必须先做上界检查
                //（键名是短字符串，AIGC 远小于 64KB；越界即结构畸形 → 无法判定）
                if (keySize < 8 || keySize > 0x10000)
                {
                    state.Signals.Add(new ForensicSignal(SignalKind.StructureMalformed, null,
                        $"keys entry {i + 1} declares unreasonable key size {keySize}"));
                    return;
                }
                reader.ReadUInt32BE("key namespace"); // 'mdta'；命名空间不区分（键名才是语义标识）
                int nameLen = (int)keySize - 8;
                byte[] name = reader.ReadExactly(nameLen, "key name");
                int nul = Array.IndexOf(name, (byte)0);
                int len = nul >= 0 ? nul : nameLen;
                state.MetaKeys[i + 1] = Encoding.ASCII.GetString(name, 0, len);
            }
        }

        /// <summary>
        /// ilst 框：若干条目 { item_size(u32)，item_type(u32 序号，对应 keys 映射)，子 box（含 data） }；
        /// data box = size + 'data' + version/flags(4，数据类型指示) + locale(4) + payload。
        /// 键名为 AIGC 的条目 payload 即附录 E JSON（TC260-PG-20257A）。
        /// </summary>
        private static void ParseIlst(BoundedReader reader, long dataSize, ScanState state)
        {
            long consumed = 0;
            while (consumed + 8 <= dataSize)
            {
                reader.CountStructure();
                long itemOffset = reader.Position;
                uint itemSize = reader.ReadUInt32BE("ilst item size");
                byte[] itemType = reader.ReadExactly(4, "ilst item type");
                if (itemSize < 8 || itemSize - 8 > dataSize - consumed - 8)
                {
                    state.Signals.Add(new ForensicSignal(SignalKind.StructureMalformed, null,
                        $"ilst item at offset {itemOffset} declares size {itemSize} beyond container"));
                    return;
                }
                uint index = ((uint)itemType[0] << 24) | ((uint)itemType[1] << 16)
                    | ((uint)itemType[2] << 8) | itemType[3];
                state.MetaKeys.TryGetValue(index, out string? keyName);

                long remaining = itemSize - 8;
                while (remaining >= 8)
                {
                    long subStart = reader.Position;
                    uint subSize = reader.ReadUInt32BE("ilst sub size");
                    byte[] subTypeBytes = reader.ReadExactly(4, "ilst sub type");
                    string subType = Encoding.ASCII.GetString(subTypeBytes);
                    remaining -= 8;
                    if (subSize < 8 || subSize - 8 > remaining)
                    {
                        state.Signals.Add(new ForensicSignal(SignalKind.StructureMalformed, null,
                            $"ilst item at offset {itemOffset} contains sub box beyond bounds"));
                        return;
                    }
                    long subData = subSize - 8;
                    if (subType == "data" && subData >= 8)
                    {
                        reader.ReadUInt32BE("data version/flags"); // 数据类型指示（1=UTF-8 等），M1 不区分
                        reader.ReadUInt32BE("data locale");
                        long payloadLen = subData - 8;
                        long payloadOffset = reader.Position;
                        if (keyName == "AIGC" && payloadLen > 0)
                        {
                            byte[] payload = reader.ReadExactly(payloadLen, "ilst AIGC payload");
                            state.Sites.Add(new LabelSite(
                                new SiteLocation(new List<object> { "ilst", state.IlstIndex++ },
                                    payloadOffset, payload.Length),
                                PayloadEncoding.Json,
                                payload));
                            state.SawUdtaAigc = true;
                        }
                    }
                    reader.Seek(subStart + subSize);
                    remaining -= subData;
                }
                consumed += itemSize;
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
