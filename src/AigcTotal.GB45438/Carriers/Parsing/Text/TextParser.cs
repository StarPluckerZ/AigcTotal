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
    /// → 首/尾显式标识的要素组合匹配。
    /// GB 45438-2025 第 5.1 条：文字形式的显式标识应同时包含人工智能要素（"人工智能"或"AI"）
    /// 与生成合成要素（"生成"和/或"合成"）——标准不规定固定文案，故按要素组合判定；
    /// 匹配窗口为去首尾空白后的前/后 64 字符（"起始位置/末尾位置"的工程界定，写入文档）。
    /// 角标形式（仅含 "AI"）不单独判定：无法与普通英文文本区分，宁 skip 不误判。
    /// </summary>
    public sealed class TextParser : ICarrierParser
    {
        private const int AffixWindow = 64;

        private static readonly string[] AiTokens = { "人工智能", "AI" };
        private static readonly string[] GenTokens = { "生成", "合成" };

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public CarrierKind Kind => CarrierKind.Text;

        public CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            reader.Seek(0);
            long take = Math.Min(reader.Length, options.Security.MaxTotalRead);
            byte[] bytes = reader.ReadAtMost(take);
            if (bytes.Length < reader.Length)
            {
                throw new CarrierLimitException(LimitKind.TotalRead,
                    $"text input exceeds MaxTotalRead {options.Security.MaxTotalRead}");
            }

            // 编码探测与解码
            string text;
            int bomBytes;
            Encoding encoding;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encoding = Encoding.UTF8;
                bomBytes = 3;
                text = encoding.GetString(bytes, 3, bytes.Length - 3);
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encoding = Encoding.Unicode;
                bomBytes = 2;
                text = encoding.GetString(bytes, 2, bytes.Length - 2);
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encoding = Encoding.BigEndianUnicode;
                bomBytes = 2;
                text = encoding.GetString(bytes, 2, bytes.Length - 2);
            }
            else
            {
                try
                {
                    text = StrictUtf8.GetString(bytes);
                }
                catch (DecoderFallbackException)
                {
                    // 文本兜底探测到二进制内容：无法判定（不能假装是文本）
                    var errorChecks = new List<CheckResult>
                    {
                        new CheckResult(CheckIds.CarrierDetect, CheckOutcome.Error,
                            code: CheckCodes.UnknownFormat, detail: "not a text file"),
                    };
                    return new CarrierScan(EmptySites, EmptySignals, errorChecks);
                }
                encoding = Encoding.UTF8;
                bomBytes = 0;
            }

            var sites = new List<LabelSite>();
            var checks = new List<CheckResult> { new CheckResult(CheckIds.CarrierDetect, CheckOutcome.Pass) };

            string trimmedStart = text.TrimStart();
            string trimmedEnd = text.TrimEnd();

            // 前缀窗口匹配
            if (TryMatchAffix(text, trimmedStart, isSuffix: false, encoding, bomBytes, out LabelSite? prefixSite))
            {
                sites.Add(prefixSite!);
                checks.Add(new CheckResult(CheckIds.TextExplicitPrefix, CheckOutcome.Pass));
            }
            else
            {
                checks.Add(new CheckResult(CheckIds.TextExplicitPrefix, CheckOutcome.Skip));
            }

            // 后缀窗口匹配
            if (TryMatchAffix(text, trimmedEnd, isSuffix: true, encoding, 0, out LabelSite? suffixSite))
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

        /// <summary>要素组合判定：窗口内同时含人工智能要素与生成合成要素（5.1 b)）。</summary>
        private static bool TryMatchAffix(string text, string trimmed, bool isSuffix,
            Encoding encoding, int bomBytes, out LabelSite? site)
        {
            site = null;
            if (trimmed.Length == 0) return false;

            string window = trimmed.Length <= AffixWindow
                ? trimmed
                : isSuffix
                    ? trimmed.Substring(trimmed.Length - AffixWindow)
                    : trimmed.Substring(0, AffixWindow);

            int aiEnd = IndexOfAnyToken(window, AiTokens, out int _);
            int genEnd = IndexOfAnyToken(window, GenTokens, out int _);
            if (aiEnd < 0 || genEnd < 0) return false;

            // 站点区域 = 窗口内从起点到两要素中较晚结束者
            int windowChars = Math.Max(aiEnd, genEnd);
            int charOffset = isSuffix
                ? text.Length - trimmed.Length + (trimmed.Length - window.Length)
                : text.Length - trimmed.Length;
            long byteOffset = bomBytes + encoding.GetByteCount(text.ToCharArray(), 0, charOffset);
            byte[] payload = Encoding.UTF8.GetBytes(window.Substring(0, windowChars));
            site = new LabelSite(
                new SiteLocation(new List<object> { isSuffix ? "suffix" : "prefix", 0 }, byteOffset, payload.Length),
                PayloadEncoding.PromptPattern,
                payload);
            return true;
        }

        /// <summary>返回任一 token 在文本中出现的结束下标（AI 忽略大小写），无命中返回 -1。</summary>
        private static int IndexOfAnyToken(string text, string[] tokens, out int tokenLen)
        {
            foreach (string token in tokens)
            {
                int idx = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    tokenLen = token.Length;
                    return idx + token.Length;
                }
            }
            tokenLen = 0;
            return -1;
        }

        private static readonly LabelSite[] EmptySites = System.Array.Empty<LabelSite>();
        private static readonly ForensicSignal[] EmptySignals = System.Array.Empty<ForensicSignal>();
    }
}
