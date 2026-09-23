using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AigcTotal.GB45438.Carriers.Parsing;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Carriers.Parsing.Wav
{
    /// <summary>
    /// WAV（RIFF/WAVE，小端）解析：遍历 chunk，枚举 AIGC chunk 的 JSON 负载；
    /// 奇数长度 chunk 的填充字节按规范跳过。TC260-PG-202510A 对 WAV 仅规定 'AIGC' chunk
    /// 裸 JSON（无位置规定），无 LIST/INFO 条款——不设降险通道。
    /// </summary>
    public sealed class WavParser : ICarrierParser
    {
        public CarrierKind Kind => CarrierKind.Wav;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            reader.Seek(0);
            byte[] riff = reader.ReadExactly(4, "RIFF");
            uint riffSize = reader.ReadUInt32LE("RIFF size");
            reader.ReadExactly(4, "WAVE"); // 魔数已由探测器确认

            long riffEnd = Math.Min(reader.Length, 8L + riffSize);
            var sites = new List<LabelSite>();
            var signals = new List<ForensicSignal>();
            int aigcIndex = 0;
            bool sawAigc = false;

            while (reader.Position < riffEnd)
            {
                ct.ThrowIfCancellationRequested();
                reader.CountStructure();

                long chunkOffset = reader.Position;
                long available = riffEnd - chunkOffset;
                if (available < 8)
                {
                    signals.Add(new ForensicSignal(SignalKind.StructureMalformed, null,
                        $"{available} trailing bytes in RIFF at offset {chunkOffset}"));
                    break;
                }

                byte[] idBytes = reader.ReadExactly(4, "chunk id");
                string chunkId = Encoding.ASCII.GetString(idBytes);
                uint chunkSize = reader.ReadUInt32LE("chunk size");
                long dataStart = reader.Position;

                if (chunkSize > available - 8)
                {
                    signals.Add(new ForensicSignal(SignalKind.StructureTruncated, null,
                        $"chunk {chunkId} at offset {chunkOffset} declares {chunkSize} bytes but only {available - 8} available"));
                    break;
                }

                if (chunkId == "AIGC")
                {
                    byte[] payload = reader.ReadExactly(chunkSize, "AIGC payload");
                    if (payload.Length == 0)
                    {
                        signals.Add(new ForensicSignal(SignalKind.MetadataShellEmpty,
                            new SiteLocation(new List<object> { "AIGC", aigcIndex++ }, dataStart, 0),
                            "AIGC chunk with empty payload"));
                    }
                    else
                    {
                        sites.Add(new LabelSite(
                            new SiteLocation(new List<object> { "AIGC", aigcIndex++ }, dataStart, payload.Length),
                            PayloadEncoding.Json,
                            payload));
                        sawAigc = true;
                    }
                }

                long pad = chunkSize % 2; // 奇数长度 chunk 后有 1 字节填充
                reader.Seek(Math.Min(dataStart + chunkSize + pad, reader.Length));
            }

            var checks = new List<CheckResult>
            {
                sawAigc
                    ? new CheckResult(CheckIds.WavRiffAigc, CheckOutcome.Pass)
                    : new CheckResult(CheckIds.WavRiffAigc, CheckOutcome.Skip),
            };
            return new CarrierScan(sites, signals, checks);
        }
    }
}
