using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AigcTotal.GB45438.Carriers.Parsing;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Carriers.Parsing.Flac
{
    /// <summary>
    /// FLAC 解析（TC260-PG-202510A）：元数据块类型 4（VORBIS_COMMENT），
    /// key=AIGC 条目的 value 为附录 E JSON。块结构：1 字节（bit7=末块 + 7bit 类型）+ 3 字节 BE 长度。
    /// </summary>
    public sealed class FlacParser : ICarrierParser
    {
        private const byte BlockTypeVorbisComment = 4;

        public CarrierKind Kind => CarrierKind.Flac;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            reader.Seek(0);
            reader.ReadExactly(4, "fLaC"); // 魔数已由探测器确认

            var sites = new List<LabelSite>();
            var signals = new List<ForensicSignal>();
            var checks = new List<CheckResult>();
            bool sawAigc = false;

            while (reader.Position < reader.Length)
            {
                ct.ThrowIfCancellationRequested();
                reader.CountStructure();

                // 头 4 字节整体读取：EOF 落在头内 → 截断诊断（不可逐字节 ReadAtMost 索引，EOF 返回空数组）
                byte[] header = reader.ReadExactly(4, "metadata block header");
                bool isLast = (header[0] & 0x80) != 0;
                byte blockType = (byte)(header[0] & 0x7F);
                uint blockLen = (uint)(header[1] << 16 | header[2] << 8 | header[3]);
                long blockStart = reader.Position;

                if (blockType == BlockTypeVorbisComment)
                {
                    byte[] data = reader.ReadExactly(blockLen, "VORBIS_COMMENT");
                    if (TryExtractAigc(data, out byte[] payload))
                    {
                        sites.Add(new LabelSite(
                            new SiteLocation(new List<object> { "VORBIS_COMMENT", 0 }, blockStart, data.Length),
                            PayloadEncoding.Json,
                            payload));
                        sawAigc = true;
                    }
                }
                else
                {
                    reader.Seek(blockStart + blockLen);
                }

                if (isLast) break;
            }

            checks.Add(sawAigc
                ? new CheckResult(CheckIds.FlacVorbisAigc, CheckOutcome.Pass)
                : new CheckResult(CheckIds.FlacVorbisAigc, CheckOutcome.Skip));
            return new CarrierScan(sites, signals, checks);
        }

        /// <summary>VORBIS_COMMENT：vendor_len(u32LE) + vendor + count(u32LE) + N × { len(u32LE) + "KEY=value" }。</summary>
        internal static bool TryExtractAigc(byte[] data, out byte[] payload)
        {
            payload = System.Array.Empty<byte>();
            int pos = 0;
            if (data.Length < 8) return false;
            uint vendorLen = ReadU32LE(data, ref pos);
            if (vendorLen > data.Length - pos) return false;
            pos += (int)vendorLen;
            if (data.Length - pos < 4) return false;
            uint count = ReadU32LE(data, ref pos);
            if (count > 100_000) return false;

            for (uint i = 0; i < count; i++)
            {
                if (data.Length - pos < 4) return false;
                uint len = ReadU32LE(data, ref pos);
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

        internal static uint ReadU32LE(byte[] data, ref int pos)
        {
            uint v = data[pos] | ((uint)data[pos + 1] << 8) | ((uint)data[pos + 2] << 16) | ((uint)data[pos + 3] << 24);
            pos += 4;
            return v;
        }
    }
}
