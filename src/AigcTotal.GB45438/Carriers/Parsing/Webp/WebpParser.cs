using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AigcTotal.GB45438.Carriers.Parsing;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Carriers.Parsing.Webp
{
    /// <summary>
    /// WebP 解析（TC260-PG-20259A）：RIFF('WEBP ') 容器中标签名为 XMP 的 chunk
    ///（fourcc "XMP "），负载为 XMP 包（走 XMP 解码器，TC260 包装层同样适用）。
    /// </summary>
    public sealed class WebpParser : ICarrierParser
    {
        public CarrierKind Kind => CarrierKind.Webp;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            reader.Seek(0);
            reader.ReadExactly(4, "RIFF");
            uint riffSize = reader.ReadUInt32LE("RIFF size");
            reader.ReadExactly(4, "WEBP");
            long riffEnd = Math.Min(reader.Length, 8L + riffSize);

            var sites = new List<LabelSite>();
            var signals = new List<ForensicSignal>();
            var checks = new List<CheckResult>();
            int chunkIndex = 0;
            bool sawXmp = false;

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
                string fourcc = Encoding.ASCII.GetString(reader.ReadExactly(4, "chunk fourcc"));
                uint chunkSize = reader.ReadUInt32LE("chunk size");
                long dataStart = reader.Position;
                if (chunkSize > riffEnd - dataStart)
                {
                    signals.Add(new ForensicSignal(SignalKind.StructureTruncated, null,
                        $"chunk {fourcc} at offset {chunkOffset} declares {chunkSize} bytes beyond RIFF"));
                    break;
                }

                if (fourcc == "XMP ")
                {
                    byte[] payload = reader.ReadExactly(chunkSize, "XMP payload");
                    if (payload.Length == 0)
                    {
                        signals.Add(new ForensicSignal(SignalKind.MetadataShellEmpty,
                            new SiteLocation(new List<object> { "XMP", chunkIndex }, dataStart, 0),
                            "XMP chunk with empty payload"));
                    }
                    else
                    {
                        sites.Add(new LabelSite(
                            new SiteLocation(new List<object> { "XMP", chunkIndex }, dataStart, payload.Length),
                            PayloadEncoding.XmpAigc,
                            payload));
                        sawXmp = true;
                    }
                }

                long pad = chunkSize % 2;
                reader.Seek(Math.Min(dataStart + chunkSize + pad, reader.Length));
                chunkIndex++;
            }

            checks.Add(sawXmp
                ? new CheckResult(CheckIds.WebpXmpAigc, CheckOutcome.Pass)
                : new CheckResult(CheckIds.WebpXmpAigc, CheckOutcome.Skip));
            return new CarrierScan(sites, signals, checks);
        }
    }
}
