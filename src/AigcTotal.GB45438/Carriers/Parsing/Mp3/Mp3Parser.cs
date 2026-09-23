using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AigcTotal.GB45438.Carriers.Parsing;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Carriers.Parsing.Mp3
{
    /// <summary>
    /// MP3（ID3v2.3/v2.4）解析：遍历帧，枚举 TXXX(描述=AIGC) 的文本负载。
    /// 帧长：v2.4 为 syncsafe，v2.3 为普通 u32BE；文本编码 0(Latin-1)/3(UTF-8) 为 M1 支持面。
    /// unsynchronisation 标志置位时按原始字节处理（罕见路径，待样本校准）。
    /// </summary>
    public sealed class Mp3Parser : ICarrierParser
    {
        public CarrierKind Kind => CarrierKind.Mp3;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            reader.Seek(0);
            var sites = new List<LabelSite>();
            var signals = new List<ForensicSignal>();

            byte[] header = reader.ReadExactly(10, "ID3 header"); // 探测器已确认 "ID3"
            byte versionMajor = header[3];
            byte flags = header[5];
            uint tagSize = Syncsafe(header, 6);
            string detail = $"ID3 2.{versionMajor}";

            if (versionMajor < 2 || versionMajor > 4)
            {
                signals.Add(new ForensicSignal(SignalKind.StructureMalformed, null,
                    $"ID3 version 2.{versionMajor} is not a defined revision"));
                return new CarrierScan(sites, signals, new List<CheckResult>
                {
                    new CheckResult(CheckIds.Mp3Id3Txxx, CheckOutcome.Error, code: CheckCodes.StructureMalformed),
                }, detail);
            }
            if (versionMajor == 2)
            {
                // v2.2 为 3 字节帧 ID 的旧格式：按 v2.3/2.4 布局错位解析会得到垃圾——
                // 显式报不支持（error → 无法判定），绝不出假 not_found
                return new CarrierScan(sites, signals, new List<CheckResult>
                {
                    new CheckResult(CheckIds.Mp3Id3Txxx, CheckOutcome.Error,
                        code: CheckCodes.ValueUnsupported, detail: "ID3v2.2 frames (3-byte IDs) not supported"),
                }, detail);
            }

            long tagEnd = Math.Min(reader.Length, 10L + tagSize);

            if ((flags & 0x40) != 0)
            {
                // 扩展头：v2.4 尺寸为 syncsafe（且不含自身 4 字节），v2.3 为普通 u32BE（含自身）
                uint extSize = versionMajor >= 4 ? ReadSyncsafe32(reader) : reader.ReadUInt32BE("ext size");
                long skip = versionMajor >= 4 ? Math.Max(0, (long)extSize) : Math.Max(0, (long)extSize - 4);
                reader.Seek(reader.Position + skip);
            }

            int frameIndex = 0;
            bool sawAigc = false;

            while (reader.Position < tagEnd)
            {
                ct.ThrowIfCancellationRequested();
                reader.CountStructure();

                long frameOffset = reader.Position;
                long available = tagEnd - frameOffset;
                if (available < 10)
                {
                    break; // 尾部填充区
                }

                byte[] idBytes = reader.ReadExactly(4, "frame id");
                if (idBytes[0] == 0)
                {
                    break; // 填充（零字节）
                }
                string frameId = Encoding.ASCII.GetString(idBytes);
                uint size = versionMajor >= 4
                    ? ReadSyncsafe32(reader)
                    : reader.ReadUInt32BE("frame size");
                reader.ReadUInt16BE("frame flags");

                if (size > available - 10)
                {
                    signals.Add(new ForensicSignal(SignalKind.StructureTruncated, null,
                        $"frame {frameId} at offset {frameOffset} declares {size} bytes but only {available - 10} available"));
                    break;
                }

                byte[] data = reader.ReadExactly(size, $"frame {frameId}");

                if (frameId == "TXXX")
                {
                    int current = frameIndex;
                    frameIndex++;
                    if (TryExtractAigc(data, out byte[] payload, out string? error))
                    {
                        sites.Add(new LabelSite(
                            new SiteLocation(new List<object> { "TXXX", current }, frameOffset, 10L + data.Length),
                            PayloadEncoding.Json,
                            payload));
                        sawAigc = true;
                    }
                    else if (error != null)
                    {
                        signals.Add(new ForensicSignal(SignalKind.StructureMalformed, null,
                            $"TXXX at offset {frameOffset}: {error}"));
                    }
                }
            }

            var checks = new List<CheckResult>
            {
                sawAigc
                    ? new CheckResult(CheckIds.Mp3Id3Txxx, CheckOutcome.Pass)
                    : new CheckResult(CheckIds.Mp3Id3Txxx, CheckOutcome.Skip),
            };
            return new CarrierScan(sites, signals, checks, detail);
        }

        private static bool TryExtractAigc(byte[] data, out byte[] payload, out string? error)
        {
            payload = System.Array.Empty<byte>();
            error = null;
            if (data.Length < 1)
            {
                error = "empty frame";
                return false;
            }
            byte encoding = data[0];
            if (encoding != 0 && encoding != 3)
            {
                error = $"text encoding {encoding} not supported yet";
                return false;
            }

            int terminatorSize = 1; // enc 0/3：单字节 NUL
            int terminator = IndexOfTerminator(data, 1, terminatorSize);
            if (terminator < 0)
            {
                error = "description not NUL-terminated";
                return false;
            }
            byte[] descBytes = new byte[terminator - 1];
            Array.Copy(data, 1, descBytes, 0, descBytes.Length);
            string description = encoding == 3
                ? Encoding.UTF8.GetString(descBytes)
                : Encoding.ASCII.GetString(descBytes);
            if (description != "AIGC")
            {
                return false; // 非 AIGC 帧：正常跳过，非错误
            }

            int payloadStart = terminator + terminatorSize;
            payload = new byte[data.Length - payloadStart];
            Array.Copy(data, payloadStart, payload, 0, payload.Length);
            return payload.Length > 0;
        }

        private static int IndexOfTerminator(byte[] data, int start, int size)
        {
            for (int i = start; i + size <= data.Length; i++)
            {
                bool allZero = true;
                for (int k = 0; k < size; k++)
                {
                    if (data[i + k] != 0)
                    {
                        allZero = false;
                        break;
                    }
                }
                if (allZero) return i;
            }
            return -1;
        }

        private static uint ReadSyncsafe32(BoundedReader reader)
        {
            byte[] b = reader.ReadExactly(4, "syncsafe size");
            return ((uint)(b[0] & 0x7F) << 21) | ((uint)(b[1] & 0x7F) << 14)
                | ((uint)(b[2] & 0x7F) << 7) | (uint)(b[3] & 0x7F);
        }

        private static uint Syncsafe(byte[] header, int offset)
        {
            return ((uint)(header[offset] & 0x7F) << 21) | ((uint)(header[offset + 1] & 0x7F) << 14)
                | ((uint)(header[offset + 2] & 0x7F) << 7) | (uint)(header[offset + 3] & 0x7F);
        }
    }
}
