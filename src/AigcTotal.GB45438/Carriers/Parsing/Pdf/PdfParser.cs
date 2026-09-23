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
    /// 采用有界裸扫描（不解析 xref/对象流）：Info 字典按惯例位于文件头部或紧邻 trailer 的文件尾，
    /// 故把 MaxTotalRead 对半分为头/尾两个窗口扫描，文件不超预算时等价全扫。
    /// 超大 PDF 中部（既不在头也不在尾窗口）的 Info 字典不可见——此类文件报 not_found 属已知局限，写入文档。
    /// 对象流压缩形态的 Info 字典同样不可见。
    /// </summary>
    public sealed class PdfParser : ICarrierParser
    {
        private static readonly byte[] Needle = Encoding.ASCII.GetBytes("/AIGC");

        public CarrierKind Kind => CarrierKind.Pdf;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            reader.Seek(0);
            long length = reader.Length;
            // 预算扣除探测阶段已读取的头部字节，剩余对半分给头/尾窗口
            long budget = Math.Max(1, options.Security.MaxTotalRead - reader.TotalRead);
            long half = budget / 2;

            // 头窗口 [0, headLen)；尾窗口 [length - tailLen, length)，与头窗口不重叠
            long headLen = Math.Min(length, Math.Max(1, half));
            byte[] head = reader.ReadAtMost(headLen);
            long tailStart = headLen;
            byte[] tail = EmptyBuffer;
            if (length > headLen)
            {
                long remainingBudget = Math.Max(1, options.Security.MaxTotalRead - reader.TotalRead);
                tailStart = Math.Max(headLen, length - remainingBudget);
                reader.Seek(tailStart);
                tail = reader.ReadAtMost(length - tailStart);
            }

            var sites = new List<LabelSite>();
            var signals = new List<ForensicSignal>();
            var checks = new List<CheckResult>();
            bool sawAigc = false;

            sawAigc = TryScanWindow(head, 0, sites) || TryScanWindow(tail, tailStart, sites);

            checks.Add(sawAigc
                ? new CheckResult(CheckIds.PdfInfoAigc, CheckOutcome.Pass)
                : new CheckResult(CheckIds.PdfInfoAigc, CheckOutcome.Skip));
            return new CarrierScan(sites, signals, checks);
        }

        /// <summary>在窗口内定位 /AIGC 字面字符串；windowStart 为窗口首字节的文件内绝对偏移。</summary>
        private static bool TryScanWindow(byte[] window, long windowStart, List<LabelSite> sites)
        {
            int searchFrom = 0;
            int match;
            while ((match = IndexOf(window, Needle, searchFrom)) >= 0)
            {
                if (TryParseLiteralString(window, match + Needle.Length, out byte[] literal, out int endPos))
                {
                    sites.Add(new LabelSite(
                        new SiteLocation(new List<object> { "AIGC", 0 }, windowStart + match, endPos - match),
                        PayloadEncoding.Json,
                        literal));
                    return true;
                }
                searchFrom = match + Needle.Length;
            }
            return false;
        }

        private static readonly byte[] EmptyBuffer = System.Array.Empty<byte>();

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
