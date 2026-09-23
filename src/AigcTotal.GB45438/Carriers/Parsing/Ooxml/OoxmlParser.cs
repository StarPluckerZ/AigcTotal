using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Xml;
using AigcTotal.GB45438.Carriers.Parsing;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Carriers.Parsing.Ooxml
{
    /// <summary>
    /// OOXML 文档族解析（TC260-PG-20258A，docx/pptx/xlsx/dotx/xltx/potx）：
    /// 定位 docProps/custom.xml，元素 property[@name="AIGC"] 的子元素文本为附录 E JSON。
    /// 主路径走 ZIP 中央目录（EOCD 尾部定位 → CD 条目 → 本地头偏移 → 精确 csize 读取）：
    /// 长度取自权威元数据，天然免疫 data-descriptor 流式条目与自解压前缀。
    /// EOCD/CD 缺失或损坏 → 回退本地头顺序遍历（截断流仍可达）。
    /// 零第三方依赖：手写结构解析 + BCL DeflateStream。
    /// </summary>
    public sealed class OoxmlParser : ICarrierParser
    {
        private const string TargetEntry = "docProps/custom.xml";
        private const int EocdSize = 22;
        private const int MaxEocdWindow = EocdSize + 64 * 1024; // EOCD + 注释长度上限 65535

        public CarrierKind Kind => CarrierKind.Ooxml;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            return TryScanCentralDirectory(reader, options, ct, out CarrierScan? scan)
                ? scan!
                : ScanLocalHeaders(reader, options, ct);
        }

        /// <summary>
        /// 中央目录主路径。结构性异常/资源限制/CD 损坏（含签名不符、zip64 标记值）→ false 回退；
        /// 干净走完但无目标条目 → 合法包的 not_found 终态（true，不再回退）。
        /// </summary>
        private static bool TryScanCentralDirectory(BoundedReader reader, VerifyOptions options,
            CancellationToken ct, out CarrierScan? scan)
        {
            scan = null;
            try
            {
                long searchStart = Math.Max(0, reader.Length - MaxEocdWindow);
                reader.Seek(searchStart);
                byte[] window = reader.ReadAtMost(reader.Length - searchStart);
                if (!TryLocateEocd(window, reader.Length, searchStart, out int eocdRel))
                {
                    return false;
                }

                ushort totalEntries = (ushort)(window[eocdRel + 10] | (window[eocdRel + 11] << 8));
                uint cdSize = ReadU32LE(window, eocdRel + 12);
                uint cdOffset = ReadU32LE(window, eocdRel + 16);

                // zip64 标记值（M1 不支持，文档化）或声明区域越界 → 回退
                if (cdOffset == 0xFFFFFFFFu || cdSize == 0xFFFFFFFFu || (long)cdOffset + cdSize > reader.Length)
                {
                    return false;
                }

                reader.Seek(cdOffset);
                for (int i = 0; i < totalEntries; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    reader.CountStructure();

                    byte[] header = reader.ReadExactly(46, "central directory entry");
                    if (header[0] != 0x50 || header[1] != 0x4B || header[2] != 0x01 || header[3] != 0x02)
                    {
                        return false; // CD 区域损坏 → 回退
                    }

                    ushort method = (ushort)(header[10] | (header[11] << 8));
                    uint csize = ReadU32LE(header, 20);
                    ushort fnLen = (ushort)(header[28] | (header[29] << 8));
                    uint localOffset = ReadU32LE(header, 42);
                    string fileName = Encoding.ASCII.GetString(reader.ReadExactly(fnLen, "central entry name"));
                    // extra + comment：Seek 越界 → 截断异常 → 回退
                    reader.Seek(reader.Position + (header[30] | (header[31] << 8)) + (header[32] | (header[33] << 8)));

                    if (fileName != TargetEntry)
                    {
                        continue;
                    }

                    // 目标命中。本地头签名不符/偏移越界 → CD 与实际布局矛盾 → 回退；
                    // 数据段截断 → 异常上抛 → 无法判定（回退也会在同一处截断）。
                    if (localOffset + 30 > reader.Length)
                    {
                        return false;
                    }
                    reader.Seek(localOffset);
                    byte[] lfh = reader.ReadExactly(30, "local file header");
                    if (lfh[0] != 0x50 || lfh[1] != 0x4B || lfh[2] != 0x03 || lfh[3] != 0x04)
                    {
                        return false;
                    }

                    // 数据起点用本地头自身的名字/额外段长度（CD 与本地头的 extra 可不一致）
                    long dataStart = reader.Position + (lfh[26] | (lfh[27] << 8)) + (lfh[28] | (lfh[29] << 8));
                    reader.Seek(dataStart);
                    byte[] raw = reader.ReadExactly(csize, TargetEntry);

                    var sites = new List<LabelSite>();
                    var checks = new List<CheckResult>();
                    if (TryExtractJson(method, raw, options.Security.MaxAlloc, out byte[] payload))
                    {
                        sites.Add(new LabelSite(
                            new SiteLocation(new List<object> { "custom.xml", 0 }, dataStart, raw.Length),
                            PayloadEncoding.Json,
                            payload));
                        checks.Add(new CheckResult(CheckIds.OoxmlCustomAigc, CheckOutcome.Pass));
                    }
                    else
                    {
                        // 条目存在但不可读：作为错误表达（→ 无法判定），不出假 not_found
                        checks.Add(new CheckResult(CheckIds.OoxmlCustomAigc, CheckOutcome.Error,
                            code: CheckCodes.PayloadMalformed,
                            detail: "custom.xml present but AIGC property unreadable"));
                    }
                    scan = new CarrierScan(sites, new List<ForensicSignal>(), checks);
                    return true;
                }

                // 合法 ZIP 但没有目标条目 → not_found 终态
                scan = new CarrierScan(
                    new List<LabelSite>(),
                    new List<ForensicSignal>(),
                    new List<CheckResult> { new CheckResult(CheckIds.OoxmlCustomAigc, CheckOutcome.Skip) });
                return true;
            }
            catch (CarrierStructureException)
            {
                return false; // CD 区域截断/越界等结构异常 → 回退本地头遍历
            }
            catch (CarrierLimitException)
            {
                return false; // CD 遍历触资源上限 → 回退（共享 TotalRead 计数，回退即刻触同一上限收尾）
            }
        }

        /// <summary>从窗口尾向前找 EOCD：取「注释长度声明与文件尾自洽」的最后一个候选，
        /// 拒绝注释/数据内的伪 PK\x05\x06。</summary>
        private static bool TryLocateEocd(byte[] window, long fileLength, long windowStart, out int eocdRel)
        {
            for (int i = window.Length - EocdSize; i >= 0; i--)
            {
                if (window[i] == 0x50 && window[i + 1] == 0x4B && window[i + 2] == 0x05 && window[i + 3] == 0x06)
                {
                    uint commentLen = (uint)(window[i + 20] | (window[i + 21] << 8));
                    if (windowStart + i + EocdSize + commentLen == fileLength)
                    {
                        eocdRel = i;
                        return true;
                    }
                }
            }
            eocdRel = 0;
            return false;
        }

        /// <summary>
        /// 回退路径：本地头顺序遍历（无/损坏中央目录的截断流、流式写出的裸形态）。
        /// </summary>
        private static CarrierScan ScanLocalHeaders(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            reader.Seek(0);
            var sites = new List<LabelSite>();
            var signals = new List<ForensicSignal>();
            var checks = new List<CheckResult>();
            bool sawAigc = false;
            bool targetEntryHandled = false;

            while (!targetEntryHandled && reader.Position < reader.Length)
            {
                ct.ThrowIfCancellationRequested();
                reader.CountStructure();

                long sigOffset = reader.Position;
                byte[] header = reader.ReadAtMost(30);
                if (header.Length < 4 || header[0] != 0x50 || header[1] != 0x4B)
                {
                    signals.Add(new ForensicSignal(SignalKind.StructureMalformed, null,
                        $"invalid zip signature at offset {sigOffset}"));
                    break;
                }
                if (header[2] == 0x01 || header[2] == 0x05) break; // 中央目录 / EOCD

                if (header.Length < 30)
                {
                    signals.Add(new ForensicSignal(SignalKind.StructureTruncated, null,
                        "local file header truncated"));
                    break;
                }
                ushort flags = (ushort)(header[6] | (header[7] << 8));
                ushort method = (ushort)(header[8] | (header[9] << 8));
                uint csize = (uint)(header[18] | (header[19] << 8) | (header[20] << 16) | (header[21] << 24));
                ushort fnLen = (ushort)(header[26] | (header[27] << 8));
                ushort extraLen = (ushort)(header[28] | (header[29] << 8));
                string fileName = Encoding.ASCII.GetString(reader.ReadExactly(fnLen, "entry name"));
                reader.Seek(reader.Position + extraLen);
                long dataStart = reader.Position;

                if (fileName == TargetEntry)
                {
                    targetEntryHandled = true;
                    byte[] raw;
                    if (csize == 0 && (flags & 0x08) != 0)
                    {
                        // data-descriptor（本地头 csize 未知）：一次性读入剩余区域在内存中定位下一签名；
                        // 越出 MaxAlloc 仍无签名 → 资源上限诊断（原逐字节 Seek+ReadExactly 扫描
                        // 在同样输入上是数百万次流调用——秒级 DoS，见 RobustnessTests 回归用例）
                        long remaining = reader.Length - dataStart;
                        long take = Math.Min(remaining, options.Security.MaxAlloc);
                        byte[] tail = reader.ReadAtMost(take);
                        int hit = IndexOfZipSignature(tail);
                        if (hit < 0 && remaining > take)
                        {
                            throw new CarrierLimitException(LimitKind.Alloc,
                                $"descriptor entry tail beyond MaxAlloc {options.Security.MaxAlloc} lacks signature");
                        }
                        raw = new byte[hit >= 0 ? hit : tail.Length];
                        Array.Copy(tail, raw, raw.Length);
                    }
                    else
                    {
                        raw = reader.ReadExactly(csize, TargetEntry);
                    }
                    if (TryExtractJson(method, raw, options.Security.MaxAlloc, out byte[] payload))
                    {
                        sites.Add(new LabelSite(
                            new SiteLocation(new List<object> { "custom.xml", 0 }, dataStart, raw.Length),
                            PayloadEncoding.Json,
                            payload));
                        sawAigc = true;
                    }
                    else
                    {
                        // 条目存在但不可读：作为错误表达（→ 无法判定），不出假 not_found
                        checks.Add(new CheckResult(CheckIds.OoxmlCustomAigc, CheckOutcome.Error,
                            code: CheckCodes.PayloadMalformed,
                            detail: "custom.xml present but AIGC property unreadable"));
                    }
                }
                else
                {
                    reader.Seek(dataStart + csize);
                }
            }

            if (checks.Count == 0)
            {
                checks.Add(sawAigc
                    ? new CheckResult(CheckIds.OoxmlCustomAigc, CheckOutcome.Pass)
                    : new CheckResult(CheckIds.OoxmlCustomAigc, CheckOutcome.Skip));
            }
            return new CarrierScan(sites, signals, checks);
        }

        /// <summary>内存中定位下一个 ZIP 结构签名（本地头 PK\x03\x04 / 中央目录 PK\x01\x02 / EOCD PK\x05\x06）。</summary>
        private static int IndexOfZipSignature(byte[] buf)
        {
            for (int i = 0; i + 4 <= buf.Length; i++)
            {
                if (buf[i] == 0x50 && buf[i + 1] == 0x4B &&
                    (buf[i + 2] == 0x03 || buf[i + 2] == 0x01 || buf[i + 2] == 0x05))
                {
                    return i;
                }
            }
            return -1;
        }

        private static uint ReadU32LE(byte[] b, int pos) =>
            (uint)(b[pos] | (b[pos + 1] << 8) | (b[pos + 2] << 16) | (b[pos + 3] << 24));

        /// <summary>按 ZIP method（0=stored / 8=deflate）解码数据，并从 custom.xml 提取
        /// property[@name="AIGC"] 的子元素文本（附录 E JSON）。解压输出以 maxInflated 封顶
        /// （deflate 炸弹：KB 级密文可膨胀 GB 级明文），越限抛 CarrierLimitException → 资源上限诊断。</summary>
        internal static bool TryExtractJson(ushort method, byte[] data, long maxInflated, out byte[] payload)
        {
            payload = System.Array.Empty<byte>();
            byte[] xmlBytes;
            if (method == 0)
            {
                xmlBytes = data;
            }
            else if (method == 8)
            {
                try
                {
                    using (var srcStream = new MemoryStream(data))
                    using (var inflate = new DeflateStream(srcStream, CompressionMode.Decompress))
                    using (var dst = new MemoryStream())
                    {
                        var buffer = new byte[64 * 1024];
                        while (true)
                        {
                            int n = inflate.Read(buffer, 0, buffer.Length);
                            if (n <= 0) break;
                            dst.Write(buffer, 0, n);
                            if (dst.Length > maxInflated)
                            {
                                throw new CarrierLimitException(LimitKind.Alloc,
                                    $"inflated size exceeds {maxInflated}");
                            }
                        }
                        xmlBytes = dst.ToArray();
                    }
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            try
            {
                using (var reader = XmlReader.Create(new MemoryStream(xmlBytes), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true,
                    IgnoreWhitespace = true,
                    XmlResolver = null,
                }))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "property") continue;
                        if (reader.GetAttribute("name") != "AIGC") continue;

                        while (reader.Read())
                        {
                            if (reader.NodeType == XmlNodeType.Element)
                            {
                                string json = reader.ReadString();
                                payload = Encoding.UTF8.GetBytes(json);
                                return payload.Length > 0;
                            }
                            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "property") break;
                        }
                        return false;
                    }
                }
                return false;
            }
            catch (XmlException)
            {
                return false;
            }
        }
    }
}
