using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AigcTotal.GB45438.Carriers.Parsing;
using AigcTotal.GB45438.Carriers.Parsing.Flac;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Carriers.Parsing.Ogg
{
    /// <summary>
    /// OGG 解析（TC260-PG-202510A，Vorbis/OPUS）：OggS 页遍历重组数据包，
    /// 第二包为注释头（\x03vorbis / OpusTags），key=AIGC 条目的 value 为附录 E JSON。
    /// 页 CRC 不校验（非标识语义，M1 从简，已记文档）。
    /// </summary>
    public sealed class OggParser : ICarrierParser
    {
        private const int PageSizeFixed = 27;

        public CarrierKind Kind => CarrierKind.Ogg;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            reader.Seek(0);

            var sites = new List<LabelSite>();
            var signals = new List<ForensicSignal>();
            var checks = new List<CheckResult>();
            var packet = new List<byte>();
            int packetIndex = 0;
            bool sawAigc = false;
            bool sawCommentPacket = false;

            while (reader.Position < reader.Length && !(sawCommentPacket || packetIndex > 8))
            {
                ct.ThrowIfCancellationRequested();
                reader.CountStructure();

                if (Encoding.ASCII.GetString(reader.ReadExactly(4, "page magic")) != "OggS")
                {
                    signals.Add(new ForensicSignal(SignalKind.StructureMalformed, null,
                        $"page at offset {reader.Position - 4} lacks OggS magic"));
                    break;
                }
                reader.ReadAtMost(1);                // version
                reader.ReadAtMost(1);                // header type
                reader.ReadExactly(8, "granule");
                reader.ReadExactly(4, "serial");
                reader.ReadExactly(4, "sequence");
                reader.ReadExactly(4, "page crc");
                byte segments = reader.ReadExactly(1, "segment count")[0]; // EOF → 截断诊断
                byte[] table = reader.ReadExactly(segments, "segment table");

                foreach (byte segLen in table)
                {
                    byte[] seg = reader.ReadAtMost(segLen);
                    packet.AddRange(seg);
                    if (segLen < 255)
                    {
                        // 包结束
                        if (packetIndex == 1 && TryExtractAigc(packet, out byte[] payload))
                        {
                            sites.Add(new LabelSite(
                                new SiteLocation(new List<object> { "VORBIS_COMMENT", 0 },
                                    reader.Position - packet.Count, packet.Count),
                                PayloadEncoding.Json,
                                payload));
                            sawAigc = true;
                        }
                        packet.Clear();
                        packetIndex++;
                        if (packetIndex > 1) { sawCommentPacket = true; break; }
                    }
                }
            }

            checks.Add(sawAigc
                ? new CheckResult(CheckIds.OggCommentAigc, CheckOutcome.Pass)
                : new CheckResult(CheckIds.OggCommentAigc, CheckOutcome.Skip));
            return new CarrierScan(sites, signals, checks);
        }

        /// <summary>注释包：\x03vorbis 或 OpusTags 魔数 + vendor + N × { len + "KEY=value" }。</summary>
        internal static bool TryExtractAigc(List<byte> packet, out byte[] payload)
        {
            payload = System.Array.Empty<byte>();
            var data = packet.ToArray();
            int pos;
            if (data.Length > 7 && data[0] == 0x03 && data[1] == (byte)'v' && data[2] == (byte)'o') pos = 7;
            else if (data.Length > 8 && data[0] == (byte)'O' && data[1] == (byte)'p') pos = 8;
            else return false;

            if (data.Length - pos < 4) return false;
            uint vendorLen = FlacParser.ReadU32LE(data, ref pos);
            if (vendorLen > data.Length - pos) return false;
            pos += (int)vendorLen;
            if (data.Length - pos < 4) return false;
            uint count = FlacParser.ReadU32LE(data, ref pos);
            if (count > 100_000) return false;

            for (uint i = 0; i < count; i++)
            {
                if (data.Length - pos < 4) return false;
                uint len = FlacParser.ReadU32LE(data, ref pos);
                if (len > (uint)(data.Length - pos)) return false;
                string entry = Encoding.UTF8.GetString(data, pos, (int)len);
                pos += (int)len;
                if (entry.Length > 5 && entry.StartsWith("AIGC=", StringComparison.Ordinal))
                {
                    payload = Encoding.UTF8.GetBytes(entry.Substring(5));
                    return payload.Length > 0;
                }
            }
            return false;
        }
    }
}
