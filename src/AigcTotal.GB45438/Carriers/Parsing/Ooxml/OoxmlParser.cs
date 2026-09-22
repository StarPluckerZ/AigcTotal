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
    /// ZIP 遍历定位 docProps/custom.xml（零第三方依赖：手写 local file header 遍历 + BCL DeflateStream），
    /// 元素 property[@name="AIGC"] 的子元素文本为附录 E JSON。
    /// </summary>
    public sealed class OoxmlParser : ICarrierParser
    {
        private const string TargetEntry = "docProps/custom.xml";

        public CarrierKind Kind => CarrierKind.Ooxml;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
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
                uint csize = (uint)(header[18] | (header[19] << 8) | (header[20] << 16) | (header[21] << 24));
                ushort fnLen = (ushort)(header[26] | (header[27] << 8));
                ushort extraLen = (ushort)(header[28] | (header[29] << 8));
                string fileName = Encoding.ASCII.GetString(reader.ReadExactly(fnLen, "entry name"));
                reader.Seek(reader.Position + extraLen);
                long dataStart = reader.Position;

                if (fileName == TargetEntry)
                {
                    targetEntryHandled = true;
                    long dataLen = (csize == 0 && (flags & 0x08) != 0)
                        ? Math.Max(0, FindNextSignature(reader, dataStart) - dataStart)
                        : csize;
                    byte[] raw = reader.ReadExactly(dataLen, TargetEntry);
                    if (TryExtractJson(flags, raw, out byte[] payload))
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

        private static long FindNextSignature(BoundedReader reader, long from)
        {
            long end = reader.Length;
            for (long pos = from; pos + 4 <= end; pos++)
            {
                reader.Seek(pos);
                byte[] b = reader.ReadExactly(4, "signature scan");
                if (b[0] == 0x50 && b[1] == 0x4B && (b[2] == 0x03 || b[2] == 0x01 || b[2] == 0x05))
                {
                    return pos;
                }
            }
            return end;
        }

        /// <summary>按 ZIP method（0=stored / 8=deflate）解码数据，并从 custom.xml 提取
        /// property[@name="AIGC"] 的子元素文本（附录 E JSON）。</summary>
        internal static bool TryExtractJson(ushort method, byte[] data, out byte[] payload)
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
                        inflate.CopyTo(dst);
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
