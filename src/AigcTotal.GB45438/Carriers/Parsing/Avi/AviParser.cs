using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AigcTotal.GB45438.Carriers.Parsing;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Carriers.Parsing.Avi
{
    /// <summary>
    /// AVI 解析（TC260-PG-20257A）：RIFF('AVI ') → LIST('INFO') 内自定义子块 ID=AIGC，
    /// 负载为 JSON + '\0' 结尾（奇数长度补齐对齐，与 WAV 同规则）。
    /// </summary>
    public sealed class AviParser : ICarrierParser
    {
        public CarrierKind Kind => CarrierKind.Avi;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            reader.Seek(0);
            reader.ReadExactly(4, "RIFF");
            uint riffSize = reader.ReadUInt32LE("RIFF size");
            reader.ReadExactly(4, "AVI ");
            long riffEnd = Math.Min(reader.Length, 8L + riffSize);

            var sites = new List<LabelSite>();
            var signals = new List<ForensicSignal>();
            var checks = new List<CheckResult>();
            bool sawAigc = false;

            while (reader.Position < riffEnd)
            {
                ct.ThrowIfCancellationRequested();
                reader.CountStructure();

                long chunkOffset = reader.Position;
                if (riffEnd - chunkOffset < 8)
                {
                    signals.Add(new ForensicSignal(SignalKind.StructureMalformed, null,
                        $"{riffEnd - chunkOffset} trailing bytes in RIFF at offset {chunkOffset}"));
                    break;
                }
                string chunkId = Encoding.ASCII.GetString(reader.ReadExactly(4, "chunk id"));
                uint chunkSize = reader.ReadUInt32LE("chunk size");
                long dataStart = reader.Position;
                if (chunkSize > riffEnd - dataStart)
                {
                    signals.Add(new ForensicSignal(SignalKind.StructureTruncated, null,
                        $"chunk {chunkId} at offset {chunkOffset} declares {chunkSize} bytes beyond RIFF"));
                    break;
                }

                if (chunkId == "LIST" && chunkSize >= 4)
                {
                    string formType = Encoding.ASCII.GetString(reader.ReadExactly(4, "LIST form type"));
                    if (formType == "INFO")
                    {
                        WalkInfo(reader, dataStart + 4, dataStart + chunkSize, sites, signals, ref sawAigc);
                    }
                }

                long pad = chunkSize % 2;
                reader.Seek(Math.Min(dataStart + chunkSize + pad, reader.Length));
            }

            checks.Add(sawAigc
                ? new CheckResult(CheckIds.AviRiffAigc, CheckOutcome.Pass)
                : new CheckResult(CheckIds.AviRiffAigc, CheckOutcome.Skip));
            return new CarrierScan(sites, signals, checks);
        }

        private static void WalkInfo(BoundedReader reader, long start, long end,
            List<LabelSite> sites, List<ForensicSignal> signals, ref bool sawAigc)
        {
            reader.Seek(start);
            while (reader.Position < end)
            {
                reader.CountStructure();
                long subOffset = reader.Position;
                if (end - subOffset < 8) break;
                string subId = Encoding.ASCII.GetString(reader.ReadExactly(4, "INFO subchunk id"));
                uint subSize = reader.ReadUInt32LE("INFO subchunk size");
                long dataStart = reader.Position;
                if (subSize > end - dataStart)
                {
                    signals.Add(new ForensicSignal(SignalKind.StructureTruncated, null,
                        $"INFO subchunk {subId} at offset {subOffset} beyond LIST"));
                    return;
                }

                if (subId == "AIGC" && subSize > 0)
                {
                    byte[] payload = reader.ReadExactly(subSize, "AIGC payload");
                    // 指南附录 D：值以 '\0' 结尾
                    int len = payload.Length;
                    if (len > 0 && payload[len - 1] == 0) len--;
                    var trimmed = new byte[len];
                    Array.Copy(payload, trimmed, len);
                    if (trimmed.Length > 0)
                    {
                        sites.Add(new LabelSite(
                            new SiteLocation(new List<object> { "LIST", 0 }, dataStart, trimmed.Length),
                            PayloadEncoding.Json,
                            trimmed));
                        sawAigc = true;
                    }
                }
                long pad = subSize % 2;
                reader.Seek(Math.Min(dataStart + subSize + pad, end));
            }
        }
    }
}
