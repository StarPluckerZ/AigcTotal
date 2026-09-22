using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AigcTotal.GB45438.Carriers.Parsing;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Carriers.Parsing.Pdf
{
    /// <summary>
    /// PDF 解析（TC260-PG-20258A）：Document Information Dictionary 的 /AIGC 键，
    /// 值为 PDF 字面字符串 (...) 内的附录 E JSON。
    /// M1 采用有界裸扫描（不解析 xref/对象流）：在前 MaxTotalRead 字节内定位 "/AIGC" 字节序列，
    /// 解析其后的 PDF 字面字符串（括号深度 + 转义）。对象流压缩形态的 Info 字典不可见——
    /// 此类文件报 not_found 属已知局限，写入文档。
    /// </summary>
    public sealed class PdfParser : ICarrierParser
    {
        private static readonly byte[] Needle = Encoding.ASCII.GetBytes("/AIGC");

        public CarrierKind Kind => CarrierKind.Pdf;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            reader.Seek(0);
            long take = Math.Min(reader.Length, options.Security.MaxTotalRead);
            byte[] bytes = reader.ReadAtMost(take);
            if (bytes.Length < reader.Length)
            {
                throw new CarrierLimitException(LimitKind.TotalRead,
                    $"pdf input exceeds MaxTotalRead {options.Security.MaxTotalRead}");
            }

            var sites = new List<LabelSite>();
            var signals = new List<ForensicSignal>();
            var checks = new List<CheckResult>();
            bool sawAigc = false;

            int searchFrom = 0;
            int match;
            while ((match = IndexOf(bytes, Needle, searchFrom)) >= 0)
            {
                if (TryParseLiteralString(bytes, match + Needle.Length, out byte[] literal, out int endPos))
                {
                    sites.Add(new LabelSite(
                        new SiteLocation(new List<object> { "AIGC", 0 }, match, endPos - match),
                        PayloadEncoding.Json,
                        literal));
                    sawAigc = true;
                    break;
                }
                searchFrom = match + Needle.Length;
            }

            checks.Add(sawAigc
                ? new CheckResult(CheckIds.PdfInfoAigc, CheckOutcome.Pass)
                : new CheckResult(CheckIds.PdfInfoAigc, CheckOutcome.Skip));
            return new CarrierScan(sites, signals, checks);
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int from)
        {
            for (int i = Math.Max(0, from); i + needle.Length <= haystack.Length; i++)
            {
                bool all = true;
                for (int k = 0; k < needle.Length; k++)
                {
                    if (haystack[i + k] != needle[k]) { all = false; break; }
                }
                if (all) return i;
            }
            return -1;
        }

        private static bool IsWhitespace(byte b) => b == (byte)' ' || b == (byte)'\r' || b == (byte)'\n' || b == (byte)'\t';

        /// <summary>从 pos 起跳过空白后解析 PDF 字面字符串 (...)（含转义与括号深度），返回原始字节。</summary>
        internal static bool TryParseLiteralString(byte[] text, int pos, out byte[] literal, out int endPos)
        {
            literal = System.Array.Empty<byte>();
            endPos = pos;
            while (pos < text.Length && IsWhitespace(text[pos])) pos++;
            if (pos >= text.Length || text[pos] != (byte)'(') return false;
            pos++;

            var sb = new List<byte>();
            int depth = 1;
            while (pos < text.Length)
            {
                byte c = text[pos++];
                if (c == (byte)'\\')
                {
                    if (pos >= text.Length) return false;
                    byte e = text[pos++];
                    switch (e)
                    {
                        case (byte)'n': sb.Add((byte)'\n'); break;
                        case (byte)'r': sb.Add((byte)'\r'); break;
                        case (byte)'t': sb.Add((byte)'\t'); break;
                        case (byte)'b': sb.Add((byte)'\b'); break;
                        case (byte)'f': sb.Add((byte)'\f'); break;
                        case (byte)'(': sb.Add((byte)'('); break;
                        case (byte)')': sb.Add((byte)')'); break;
                        case (byte)'\\': sb.Add((byte)'\\'); break;
                        default:
                            if (e >= (byte)'0' && e <= (byte)'7')
                            {
                                int value = e - (byte)'0';
                                int digits = 1;
                                while (digits < 3 && pos < text.Length && text[pos] >= (byte)'0' && text[pos] <= (byte)'7')
                                {
                                    value = value * 8 + (text[pos++] - (byte)'0');
                                    digits++;
                                }
                                sb.Add((byte)value);
                            }
                            else
                            {
                                sb.Add(e); // \<EOL> 行续接等：保字节处理
                            }
                            break;
                    }
                }
                else if (c == (byte)'(')
                {
                    depth++;
                    sb.Add(c);
                }
                else if (c == (byte)')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        literal = sb.ToArray();
                        endPos = pos;
                        return literal.Length > 0;
                    }
                    sb.Add(c);
                }
                else
                {
                    sb.Add(c);
                }
            }
            return false;
        }
    }
}
