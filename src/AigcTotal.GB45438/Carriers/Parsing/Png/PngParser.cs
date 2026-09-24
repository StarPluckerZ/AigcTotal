using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using AigcTotal.GB45438.Carriers.Parsing;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Carriers.Parsing.Png
{
    /// <summary>
    /// PNG 容器解析：遍历 chunk 序列。
    /// - 标识通道：tEXt（关键字 AIGC，JSON 负载）、zTXt（关键字 AIGC，zlib 解压后 JSON）、
    ///   iTXt（关键字 XML:com.adobe.xmp，XMP 负载，TC260 实测格式：&lt;TC260:AIGC&gt; 内嵌附录 E JSON）。
    /// - 大块（数据部分 &gt; MaxAlloc，典型为 IDAT）：流式 CRC 校验、零分配、不计入 MaxTotalRead——
    ///   与 MP4/WAV/MP3 的跳块策略一致；超大元数据块（tEXt/iTXt/zTXt）超出安全预算 → 畸形信号 → 无法判定。
    /// </summary>
    public sealed class PngParser : ICarrierParser
    {
        private const string AigcKeyword = "AIGC";
        private const string XmpKeyword = "XML:com.adobe.xmp";

        public CarrierKind Kind => CarrierKind.Png;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            reader.Seek(0);
            reader.ReadExactly(8, "PNG signature"); // 魔数已由探测器确认

            var sites = new List<LabelSite>();
            var signals = new List<ForensicSignal>();
            var checks = new List<CheckResult>();
            int textIndex = 0;
            int itxtIndex = 0;
            int ztxtIndex = 0;
            bool sawAigcText = false;
            bool sawXmpSite = false;
            bool sawEnd = false;

            while (reader.Position < reader.Length && !sawEnd)
            {
                ct.ThrowIfCancellationRequested();
                reader.CountStructure();

                long chunkHeaderOffset = reader.Position;
                long remaining = reader.Length - reader.Position;
                if (remaining < 12)
                {
                    signals.Add(new ForensicSignal(SignalKind.StructureTruncated, null,
                        $"trailing {remaining} bytes at offset {chunkHeaderOffset}"));
                    break;
                }

                uint length = reader.ReadUInt32BE("chunk length");
                byte[] type = reader.ReadExactly(4, "chunk type");
                string typeName = System.Text.Encoding.ASCII.GetString(type);
                long dataOffset = reader.Position;

                bool crcOk;
                if (IsMetadataType(typeName))
                {
                    // 元数据块（tEXt/iTXt/zTXt）：进解析器内存并逐字节 CRC。
                    // 超出 MaxAlloc 的元数据块会在 ReadExactly 处触发上限 → 无法判定（宁降档不猜测）。
                    byte[] data = reader.ReadExactly(length, $"chunk {typeName}");
                    uint storedCrc = reader.ReadUInt32BE("chunk crc");
                    crcOk = CrcMatches(type, data, storedCrc);
                    if (!crcOk)
                    {
                        signals.Add(new ForensicSignal(SignalKind.ChecksumMismatch,
                            new SiteLocation(new List<object> { typeName, textIndex }, chunkHeaderOffset, 12L + length),
                            $"CRC mismatch on chunk {typeName} at offset {chunkHeaderOffset}"));
                        // CRC 不符仍处理站点：取证优先，字段本身仍可读
                    }

                    switch (typeName)
                    {
                        case "tEXt":
                            CollectTextChunk(data, dataOffset, textIndex, sites, signals, ref sawAigcText);
                            textIndex++;
                            break;
                        case "iTXt":
                            CollectItxtChunk(data, dataOffset, itxtIndex, sites, signals, ref sawXmpSite);
                            itxtIndex++;
                            break;
                        case "zTXt":
                            CollectZtxtChunk(data, dataOffset, ztxtIndex, sites, signals,
                                options.Security.MaxAlloc, ref sawAigcText);
                            ztxtIndex++;
                            break;
                    }
                }
                else
                {
                    // 非元数据块（IDAT/IHDR/…，真实 AIGC PNG 可含上千个 8KB IDAT）：
                    // 数据永远不进内存、不计入 MaxTotalRead，仅流式 CRC 校验
                    uint running = Crc32.Update(Crc32.Initial, type);
                    running = reader.StreamCrc(running, length, $"chunk {typeName}");
                    uint storedCrc = reader.ReadUInt32BE("chunk crc");
                    crcOk = Crc32.Finalize(running) == storedCrc;
                    if (!crcOk)
                    {
                        signals.Add(new ForensicSignal(SignalKind.ChecksumMismatch,
                            new SiteLocation(new List<object> { typeName, 0 }, chunkHeaderOffset, 12L + length),
                            $"CRC mismatch on chunk {typeName} at offset {chunkHeaderOffset}"));
                    }
                }

                if (typeName == "IEND") sawEnd = true;
            }

            checks.Add(sawAigcText
                ? new CheckResult(CheckIds.PngTextAigc, CheckOutcome.Pass)
                : new CheckResult(CheckIds.PngTextAigc, CheckOutcome.Skip));
            checks.Add(sawXmpSite
                ? new CheckResult(CheckIds.PngXmpAigc, CheckOutcome.Pass)
                : new CheckResult(CheckIds.PngXmpAigc, CheckOutcome.Skip));

            return new CarrierScan(sites, signals, checks);
        }

        private static bool IsMetadataType(string typeName) =>
            typeName == "tEXt" || typeName == "iTXt" || typeName == "zTXt";

        private static void CollectTextChunk(byte[] data, long dataOffset, int index,
            List<LabelSite> sites, List<ForensicSignal> signals, ref bool sawAigcText)
        {
            int separator = Array.IndexOf(data, (byte)0);
            if (separator < 0)
            {
                signals.Add(new ForensicSignal(SignalKind.StructureMalformed,
                    new SiteLocation(new List<object> { "tEXt", index }, dataOffset, data.Length),
                    "tEXt without NUL separator"));
                return;
            }
            string keyword = System.Text.Encoding.ASCII.GetString(data, 0, separator);
            if (keyword != AigcKeyword) return;

            if (data.Length > separator + 1)
            {
                var payload = new byte[data.Length - separator - 1];
                Array.Copy(data, separator + 1, payload, 0, payload.Length);
                sites.Add(new LabelSite(
                    new SiteLocation(new List<object> { "tEXt", index }, dataOffset + separator + 1, payload.Length),
                    PayloadEncoding.Json,
                    payload));
                sawAigcText = true;
            }
            else
            {
                signals.Add(new ForensicSignal(SignalKind.MetadataShellEmpty,
                    new SiteLocation(new List<object> { "tEXt", index }, dataOffset, data.Length),
                    "tEXt with keyword AIGC but empty payload"));
            }
        }

        /// <summary>
        /// iTXt 布局：keyword NUL compressionFlag compressionMethod languageTag NUL translatedKeyword NUL text。
        /// 仅关键字 XML:com.adobe.xmp（Adobe XMP 规范写法，TC260 实测一致）作为 XMP 站点；
        /// 其余 iTXt 忽略。压缩 iTXt（flag != 0）不支持 → 畸形信号 → 无法判定（防假性 not_found）。
        /// </summary>
        private static void CollectItxtChunk(byte[] data, long dataOffset, int index,
            List<LabelSite> sites, List<ForensicSignal> signals, ref bool sawXmpSite)
        {
            // 守卫阈值 +3 与后续使用严格一致：data[kw+1] 只需 +2 ≤ Length；
            // pos = kw+3 作为 Array.IndexOf 起点（BCL 允许 start == Length，返回 -1）——
            // 若改为 +2 会让 IndexOf 以 Length+1 抛 ArgumentOutOfRangeException 逃出 Verify（zTXt P1 同型）
            int keywordEnd = Array.IndexOf(data, (byte)0);
            if (keywordEnd < 0 || keywordEnd + 3 > data.Length)
            {
                signals.Add(new ForensicSignal(SignalKind.StructureMalformed,
                    new SiteLocation(new List<object> { "iTXt", index }, dataOffset, data.Length),
                    "iTXt without NUL separator or truncated header"));
                return;
            }
            string keyword = System.Text.Encoding.ASCII.GetString(data, 0, keywordEnd);
            if (keyword != XmpKeyword) return;

            byte compressionFlag = data[keywordEnd + 1];
            if (compressionFlag != 0)
            {
                signals.Add(new ForensicSignal(SignalKind.StructureMalformed,
                    new SiteLocation(new List<object> { "iTXt", index }, dataOffset, data.Length),
                    "compressed iTXt XMP not supported"));
                return;
            }

            int pos = keywordEnd + 3;
            int languageEnd = Array.IndexOf(data, (byte)0, pos);
            if (languageEnd < 0) { ItxtMalformed(signals, index, dataOffset, data.Length); return; }
            int translatedEnd = Array.IndexOf(data, (byte)0, languageEnd + 1);
            if (translatedEnd < 0) { ItxtMalformed(signals, index, dataOffset, data.Length); return; }

            int payloadStart = translatedEnd + 1;
            if (payloadStart >= data.Length)
            {
                signals.Add(new ForensicSignal(SignalKind.MetadataShellEmpty,
                    new SiteLocation(new List<object> { "iTXt", index }, dataOffset, data.Length),
                    "iTXt XMP with empty packet"));
                return;
            }

            var payload = new byte[data.Length - payloadStart];
            Array.Copy(data, payloadStart, payload, 0, payload.Length);
            sites.Add(new LabelSite(
                new SiteLocation(new List<object> { "iTXt", index }, dataOffset + payloadStart, payload.Length),
                PayloadEncoding.XmpAigc,
                payload));
            sawXmpSite = true;
        }

        private static void ItxtMalformed(List<ForensicSignal> signals, int index, long offset, long length)
        {
            signals.Add(new ForensicSignal(SignalKind.StructureMalformed,
                new SiteLocation(new List<object> { "iTXt", index }, offset, length),
                "iTXt with malformed language/translated-keyword sections"));
        }

        /// <summary>
        /// zTXt 布局：keyword NUL compressionMethod(1) zlib 数据流（0x78 0x01 头 + deflate + Adler-32）。
        /// 关键字 AIGC 的 zTXt 经 zlib 解压后作为 JSON 站点；解压输出以 MaxAlloc 封顶
        /// （KB 级密文可膨胀 GB 级明文），越限抛 CarrierLimitException → 资源上限 → 无法判定。
        /// 畸形 zlib 流 → 畸形信号 → 无法判定（防假性 not_found）。
        /// 站点坐标指向文件内的压缩字节区（写侧手术坐标），RawPayload 为解压后的负载。
        /// </summary>
        private static void CollectZtxtChunk(byte[] data, long dataOffset, int index,
            List<LabelSite> sites, List<ForensicSignal> signals, long maxInflated, ref bool sawAigcText)
        {
            int separator = Array.IndexOf(data, (byte)0);
            if (separator < 0)
            {
                signals.Add(new ForensicSignal(SignalKind.StructureMalformed,
                    new SiteLocation(new List<object> { "zTXt", index }, dataOffset, data.Length),
                    "zTXt without NUL separator"));
                return;
            }
            string keyword = System.Text.Encoding.ASCII.GetString(data, 0, separator);
            if (keyword != AigcKeyword) return;

            // zlib 起点为 separator+4（NUL + method + 2 字节 zlib 头），长度不足即截断——
            // 2026-09-24 P1：旧守卫 separator+3 允许 1 字节流体进入 MemoryStream(count=-1) 抛越界异常逃出 Verify
            if (data.Length < separator + 4)
            {
                signals.Add(new ForensicSignal(SignalKind.StructureMalformed,
                    new SiteLocation(new List<object> { "zTXt", index }, dataOffset, data.Length),
                    "zTXt too short for compression method and zlib stream"));
                return;
            }
            byte compressionMethod = data[separator + 1];
            if (compressionMethod != 0)
            {
                signals.Add(new ForensicSignal(SignalKind.StructureMalformed,
                    new SiteLocation(new List<object> { "zTXt", index }, dataOffset, data.Length),
                    $"zTXt compression method {compressionMethod} not supported"));
                return;
            }

            // 布局：separator=NUL，separator+1=method，separator+2..3=zlib 头（CMF/FLG），
            // 自 separator+4 起为裸 deflate 流（尾部 Adler-32 由 DeflateStream 忽略）
            int zlibStart = separator + 4;
            long compressedStart = dataOffset + zlibStart;
            long compressedLength = data.Length - zlibStart;
            byte[] payload;
            try
            {
                using (var src = new MemoryStream(data, zlibStart, data.Length - zlibStart))
                using (var inflate = new DeflateStream(src, CompressionMode.Decompress))
                {
                    var output = new MemoryStream();
                    var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        int n = inflate.Read(buffer, 0, buffer.Length);
                        if (n <= 0) break;
                        output.Write(buffer, 0, n);
                        if (output.Length > maxInflated)
                        {
                            throw new CarrierLimitException(LimitKind.Alloc,
                                $"zTXt inflated size exceeds MaxAlloc {maxInflated}");
                        }
                    }
                    payload = output.ToArray();
                }
            }
            catch (InvalidDataException)
            {
                signals.Add(new ForensicSignal(SignalKind.StructureMalformed,
                    new SiteLocation(new List<object> { "zTXt", index }, dataOffset, data.Length),
                    "zTXt zlib stream invalid"));
                return;
            }

            if (payload.Length == 0)
            {
                signals.Add(new ForensicSignal(SignalKind.MetadataShellEmpty,
                    new SiteLocation(new List<object> { "zTXt", index }, compressedStart, compressedLength),
                    "zTXt with keyword AIGC but empty payload"));
                return;
            }

            sites.Add(new LabelSite(
                new SiteLocation(new List<object> { "zTXt", index }, compressedStart, compressedLength),
                PayloadEncoding.Json,
                payload));
            sawAigcText = true;
        }

        private static bool CrcMatches(byte[] type, byte[] data, uint storedCrc)
        {
            var crcInput = new byte[4 + data.Length];
            Array.Copy(type, 0, crcInput, 0, 4);
            Array.Copy(data, 0, crcInput, 4, data.Length);
            return Crc32.Compute(crcInput, 0, crcInput.Length) == storedCrc;
        }
    }
}
