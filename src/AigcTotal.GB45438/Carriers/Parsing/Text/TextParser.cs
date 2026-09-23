using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AigcTotal.GB45438.Carriers.Parsing;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Carriers.Parsing.Text
{
    /// <summary>
    /// 文本载体解析：编码探测（BOM → 严格 UTF-8；失败视为二进制误探测 → unknown_format）
    /// → ① YAML front matter 隐式标识（TC260-PG-20258A：Markdown 文件头部 AIGC 映射）
    /// → ② 首/尾显式标识的要素组合匹配。
    /// GB 45438-2025 第 5.1 条：文字形式的显式标识应同时包含人工智能要素（"人工智能"或"AI"）
    /// 与生成合成要素（"生成"和/或"合成"）——标准不规定固定文案，故按要素组合判定；
    /// 匹配窗口为去首尾空白后的前/后 64 字符（"起始位置/末尾位置"的工程界定，写入文档）。
    /// 角标形式（仅含 "AI"）不单独判定：无法与普通英文文本区分，宁 skip 不误判。
    /// 大文件工程界定：判定只依赖首尾窗口与编码合法性——头/尾窗口各取（HeadWindow/TailWindow），
    /// 中段仅做流式严格 UTF-8 校验（零保留、不占读取预算），因此任意大小的合法文本都可判定，
    /// 只有编码非法才降档（inconclusive）。
    /// </summary>
    public sealed class TextParser : ICarrierParser
    {
        private const int AffixWindow = 64;
        private const int FrontMatterMaxScan = 4096;
        private const int HeadWindow = 64 * 1024;
        private const int TailWindow = 4 * 1024;

        private static readonly string[] AiTokens = { "人工智能", "AI" };
        private static readonly string[] GenTokens = { "生成", "合成" };

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public CarrierKind Kind => CarrierKind.Text;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            reader.Seek(0);
            long length = reader.Length;

            // —— 头窗口：BOM 探测 + front matter + 前缀窗口 ——
            int headLen = (int)Math.Min(length, HeadWindow);
            byte[] head = reader.ReadAtMost(headLen);

            string headText;
            Encoding encoding;
            int bomBytes;
            bool utf16 = DetectBom(head, out bomBytes);
            if (utf16)
            {
                encoding = bomBytes == 2 && head.Length >= 2 && head[0] == 0xFF
                    ? Encoding.Unicode
                    : Encoding.BigEndianUnicode;
                // 奇数尾字节不成码元：剥掉再解码
                int payloadLen = head.Length - bomBytes;
                if (payloadLen % 2 == 1) payloadLen--;
                headText = encoding.GetString(head, bomBytes, payloadLen);
            }
            else
            {
                encoding = Encoding.UTF8;
                // DetectBom 已输出 UTF-8 BOM 字节数（无 BOM 为 0）
                // 全量严格校验先行：头窗口逐字节 + 其余流式（零保留、不占读取预算）
                var validator = new Utf8StreamValidator();
                if (!validator.Feed(head))
                {
                    return NotText();
                }
                if (head.Length < length)
                {
                    bool valid = true;
                    reader.StreamScan(length - head.Length, span =>
                    {
                        if (valid && !validator.Feed(span))
                        {
                            valid = false;
                        }
                    }, "text body");
                    if (!valid)
                    {
                        return NotText();
                    }
                }
                // 校验通过后才解码头窗口；窗口边界可能截断末尾多字节序列，剥掉再解
                //（BOM 已计入 bomBytes，解码从 bomBytes 起，使 front matter/前缀窗口从真实内容开始）
                int safeEnd = TrimTruncatedSequence(head);
                try
                {
                    headText = StrictUtf8.GetString(head, bomBytes, safeEnd - bomBytes);
                }
                catch (DecoderFallbackException)
                {
                    return NotText();
                }
            }

            var sites = new List<LabelSite>();
            var checks = new List<CheckResult> { new CheckResult(CheckIds.CarrierDetect, CheckOutcome.Pass) };

            // ① YAML front matter 隐式标识（Markdown 等，TC260-PG-20258A）
            if (TryExtractFrontMatter(headText, encoding, bomBytes, out LabelSite? frontMatterSite))
            {
                sites.Add(frontMatterSite!);
                checks.Add(new CheckResult(CheckIds.TextFrontMatterAigc, CheckOutcome.Pass));
            }
            else
            {
                checks.Add(new CheckResult(CheckIds.TextFrontMatterAigc, CheckOutcome.Skip));
            }

            // —— 尾窗口：对齐到字符边界后解码。文件不超头窗口时，尾窗口即头窗口内容本身 ——
            long tailStart = 0;
            string tailText = headText;
            if (length > head.Length)
            {
                tailText = string.Empty;
                tailStart = Math.Max(head.Length, length - TailWindow);
                if (utf16)
                {
                    // 码元对齐（2 字节）：起点必须落在字符边界
                    long offset = (tailStart - bomBytes) % 2;
                    tailStart -= offset;
                }
                else
                {
                    reader.Seek(tailStart);
                    int probe = (int)Math.Min(4, length - tailStart);
                    byte[] lead = reader.ReadAtMost(probe);
                    for (int i = 0; i < lead.Length; i++)
                    {
                        if ((lead[i] & 0xC0) != 0x80) { tailStart += i; break; } // 跳过续字节，落到首字节
                    }
                }
                reader.Seek(tailStart);
                byte[] tail = reader.ReadAtMost(length - tailStart);
                if (utf16 && tail.Length % 2 == 1)
                {
                    var even = new byte[tail.Length - 1];
                    Array.Copy(tail, even, even.Length);
                    tail = even;
                }
                tailText = encoding.GetString(tail);
            }

            // 前缀窗口匹配
            string trimmedStart = headText.TrimStart();
            if (TryMatchAffix(trimmedStart, isSuffix: false, encoding, bomBytes, windowStartAbs: 0,
                out LabelSite? prefixSite))
            {
                sites.Add(prefixSite!);
                checks.Add(new CheckResult(CheckIds.TextExplicitPrefix, CheckOutcome.Pass));
            }
            else
            {
                checks.Add(new CheckResult(CheckIds.TextExplicitPrefix, CheckOutcome.Skip));
            }

            // 后缀窗口匹配（小文件时源为头窗口文本：基准偏移含 BOM 字节；
            // 大文件时源为尾窗口文本：tailStart 已是文件内绝对偏移，不再加 BOM）
            string trimmedEnd = tailText.TrimEnd();
            bool fromTail = length > head.Length;
            if (TryMatchAffix(trimmedEnd, isSuffix: true, encoding,
                bomBytes: fromTail ? 0 : bomBytes,
                windowStartAbs: fromTail ? tailStart : 0,
                out LabelSite? suffixSite))
            {
                sites.Add(suffixSite!);
                checks.Add(new CheckResult(CheckIds.TextExplicitSuffix, CheckOutcome.Pass));
            }
            else
            {
                checks.Add(new CheckResult(CheckIds.TextExplicitSuffix, CheckOutcome.Skip));
            }

            checks.Add(sites.Count > 0
                ? new CheckResult(CheckIds.TextPromptAffix, CheckOutcome.Pass)
                : new CheckResult(CheckIds.TextPromptAffix, CheckOutcome.Skip));

            return new CarrierScan(sites, EmptySignals, checks);
        }

        /// <summary>BOM 探测：返回是否 UTF-16（决定后续路径）；UTF-8 BOM 记入 bomBytes 后走严格 UTF-8 路径。</summary>
        private static bool DetectBom(byte[] head, out int bomBytes)
        {
            if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                bomBytes = 3;
                return false;
            }
            if (head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
            {
                bomBytes = 2;
                return true;
            }
            bomBytes = 0;
            return false;
        }

        private static CarrierScan NotText()
        {
            var errorChecks = new List<CheckResult>
            {
                new CheckResult(CheckIds.CarrierDetect, CheckOutcome.Error,
                    code: CheckCodes.UnknownFormat, detail: "not a text file"),
            };
            return new CarrierScan(EmptySites, EmptySignals, errorChecks);
        }

        /// <summary>
        /// 头窗口末尾若截断多字节序列（窗口边界落在字符中间），返回剥掉该序列后的安全解码终点。
        /// 全量校验已先行通过，故除窗口边界截断外不存在非法序列。
        /// </summary>
        private static int TrimTruncatedSequence(byte[] head)
        {
            int end = head.Length;
            if (end == 0) return 0;
            int back = 0;
            while (back < 3 && end - back - 1 >= 0 && (head[end - back - 1] & 0xC0) == 0x80)
            {
                back++;
            }
            byte lead = head[end - back - 1];
            int expected = lead >= 0xF0 ? 3 : lead >= 0xE0 ? 2 : lead >= 0xC0 ? 1 : 0;
            return back < expected ? end - back - 1 : end;
        }

        /// <summary>
        /// YAML front matter 提取（TC260-PG-20258A）：首行 --- 定界的元数据区内，
        /// 存在 "AIGC:" 键时，站点负载 = AIGC 行起至缩进块结束（交给 FrontMatterDecoder 出字段）。
        /// </summary>
        private static bool TryExtractFrontMatter(string text, Encoding encoding, int bomBytes, out LabelSite? site)
        {
            site = null;
            if (!text.StartsWith("---", StringComparison.Ordinal)) return false;
            int firstLineEnd = text.IndexOf('\n');
            if (firstLineEnd < 0) return false;
            int scanLimit = Math.Min(text.Length, FrontMatterMaxScan);

            int pos = firstLineEnd + 1;
            int aigcStart = -1;
            int aigcLineEnd = -1;
            while (pos < scanLimit)
            {
                int eol = text.IndexOf('\n', pos);
                if (eol < 0 || eol > scanLimit) eol = Math.Min(text.Length, scanLimit);
                string line = text.Substring(pos, eol - pos).TrimEnd('\r');
                if (line == "---" || line == "...") return false; // front matter 结束，未见 AIGC
                if (line.StartsWith("AIGC:", StringComparison.Ordinal))
                {
                    aigcStart = pos;
                    aigcLineEnd = eol;
                    break;
                }
                pos = eol + 1;
            }
            if (aigcStart < 0) return false;

            // AIGC 映射块 = AIGC 行 + 后续缩进行（空行或非缩进行结束）
            int blockEnd = aigcLineEnd;
            int scan = aigcLineEnd + 1;
            while (scan < scanLimit)
            {
                int eol = text.IndexOf('\n', scan);
                if (eol < 0 || eol > scanLimit) eol = Math.Min(text.Length, scanLimit);
                string line = text.Substring(scan, eol - scan).TrimEnd('\r');
                if (line.Length == 0 || !(line[0] == ' ' || line[0] == '\t')) break;
                blockEnd = eol;
                scan = eol + 1;
            }

            int blockLen = blockEnd - aigcStart;
            if (blockLen <= 0) return false;
            long byteOffset = bomBytes + encoding.GetByteCount(text.ToCharArray(), 0, aigcStart);
            byte[] payload = Encoding.UTF8.GetBytes(text.Substring(aigcStart, blockLen));
            site = new LabelSite(
                new SiteLocation(new List<object> { "front_matter", 0 }, byteOffset, payload.Length),
                PayloadEncoding.FrontMatterYaml,
                payload);
            return true;
        }

        /// <summary>
        /// 要素组合判定（5.1 b)）：窗口内同时含人工智能要素与生成合成要素。
        /// 前缀窗口取自头窗口去空白后的文本（bomBytes 计入文件偏移）；
        /// 后缀窗口取自尾窗口去尾部空白后的文本（windowStartAbs 为窗口首字节在文件内的绝对偏移）。
        /// </summary>
        private static bool TryMatchAffix(string trimmed, bool isSuffix,
            Encoding encoding, int bomBytes, long windowStartAbs, out LabelSite? site)
        {
            site = null;
            if (trimmed.Length == 0) return false;

            string window = trimmed.Length <= AffixWindow
                ? trimmed
                : isSuffix
                    ? trimmed.Substring(trimmed.Length - AffixWindow)
                    : trimmed.Substring(0, AffixWindow);

            int aiEnd = IndexOfAnyToken(window, AiTokens);
            int genEnd = IndexOfAnyToken(window, GenTokens);
            if (aiEnd < 0 || genEnd < 0) return false;

            // 站点区域 = 窗口内从起点到两要素中较晚结束者
            int windowChars = Math.Max(aiEnd, genEnd);
            int windowStartChar = isSuffix ? trimmed.Length - window.Length : 0;
            long byteOffset = windowStartAbs + bomBytes
                + encoding.GetByteCount(trimmed.ToCharArray(), 0, windowStartChar);
            byte[] payload = Encoding.UTF8.GetBytes(window.ToCharArray(), 0, windowChars);
            site = new LabelSite(
                new SiteLocation(new List<object> { isSuffix ? "suffix" : "prefix", 0 }, byteOffset, payload.Length),
                PayloadEncoding.PromptPattern,
                payload);
            return true;
        }

        /// <summary>返回任一 token 在文本中出现的结束下标（AI 忽略大小写），无命中返回 -1。</summary>
        private static int IndexOfAnyToken(string text, string[] tokens)
        {
            foreach (string token in tokens)
            {
                int idx = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    return idx + token.Length;
                }
            }
            return -1;
        }

        /// <summary>严格 UTF-8 流式校验器（RFC 3629 子集：拒绝坏续字节、过长编码、代理项区、超 U+10FFFF）。</summary>
        private sealed class Utf8StreamValidator
        {
            private int _continuations;
            private uint _codePoint;
            private uint _minimum;

            public bool Feed(ReadOnlySpan<byte> chunk)
            {
                foreach (byte b in chunk)
                {
                    if (!Feed(b)) return false;
                }
                return true;
            }

            private bool Feed(byte b)
            {
                if (_continuations > 0)
                {
                    if ((b & 0xC0) != 0x80) return false;
                    _codePoint = (_codePoint << 6) | (uint)(b & 0x3F);
                    if (--_continuations == 0
                        && (_codePoint < _minimum || _codePoint > 0x10FFFF
                            || (_codePoint >= 0xD800 && _codePoint <= 0xDFFF)))
                    {
                        return false;
                    }
                    return true;
                }
                if (b < 0x80) return true;
                if (b < 0xC2) return false;               // 散落续字节 / 过长编码 0xC0-0xC1
                if (b < 0xE0) { _continuations = 1; _codePoint = (uint)(b & 0x1F); _minimum = 0x80; return true; }
                if (b < 0xF0) { _continuations = 2; _codePoint = (uint)(b & 0x0F); _minimum = 0x800; return true; }
                if (b < 0xF5) { _continuations = 3; _codePoint = (uint)(b & 0x07); _minimum = 0x10000; return true; }
                return false;                              // 0xF5..0xFF
            }
        }

        private static readonly LabelSite[] EmptySites = System.Array.Empty<LabelSite>();
        private static readonly ForensicSignal[] EmptySignals = System.Array.Empty<ForensicSignal>();
    }
}
