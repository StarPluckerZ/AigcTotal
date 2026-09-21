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
    /// → 首/尾提示语模式匹配（显式标识）。
    /// 模式清单为初始基线，待 TC260-PG-20258A《文本文件》指南原文校准后收紧。
    /// </summary>
    public sealed class TextParser : ICarrierParser
    {
        /// <summary>提示语模式（前缀/后缀共用；匹配大小写不敏感，先匹配先得）。</summary>
        private static readonly string[] PromptPatterns =
        {
            "人工智能生成", "人工智能合成", "由人工智能生成", "由人工智能合成",
            "本内容由人工智能生成", "本内容由人工智能合成", "本作品由人工智能生成",
            "AI生成", "AI合成", "由AI生成", "由AI合成", "本内容由AI生成", "本内容由AI合成", "AI创作",
        };

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

            // 前缀匹配：站点偏移 = BOM + 跳过头部空白 + 模式在原文中的字节偏移
            if (TryMatchPrefix(text, trimmedStart, encoding, bomBytes, out LabelSite? prefixSite))
            {
                sites.Add(prefixSite!);
                checks.Add(new CheckResult(CheckIds.TextExplicitPrefix, CheckOutcome.Pass));
            }
            else
            {
                checks.Add(new CheckResult(CheckIds.TextExplicitPrefix, CheckOutcome.Skip));
            }

            if (TryMatchSuffix(text, trimmedEnd, encoding, out LabelSite? suffixSite))
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

        private static bool TryMatchPrefix(string text, string trimmedStart, Encoding encoding,
            int bomBytes, out LabelSite? site)
        {
            site = null;
            foreach (string pattern in PromptPatterns)
            {
                if (trimmedStart.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    int charOffset = text.Length - trimmedStart.Length;
                    long byteOffset = bomBytes + encoding.GetByteCount(text.ToCharArray(), 0, charOffset);
                    byte[] payload = Encoding.UTF8.GetBytes(pattern);
                    site = new LabelSite(
                        new SiteLocation(new List<object> { "prefix", 0 }, byteOffset, payload.Length),
                        PayloadEncoding.PromptPattern,
                        payload);
                    return true;
                }
            }
            return false;
        }

        private static bool TryMatchSuffix(string text, string trimmedEnd, Encoding encoding, out LabelSite? site)
        {
            site = null;
            foreach (string pattern in PromptPatterns)
            {
                if (trimmedEnd.EndsWith(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    int charOffset = text.Length - trimmedEnd.Length + (trimmedEnd.Length - pattern.Length);
                    long byteOffset = encoding.GetByteCount(text.ToCharArray(), 0, charOffset);
                    byte[] payload = Encoding.UTF8.GetBytes(pattern);
                    site = new LabelSite(
                        new SiteLocation(new List<object> { "suffix", 0 }, byteOffset, payload.Length),
                        PayloadEncoding.PromptPattern,
                        payload);
                    return true;
                }
            }
            return false;
        }

        private static readonly LabelSite[] EmptySites = System.Array.Empty<LabelSite>();
        private static readonly ForensicSignal[] EmptySignals = System.Array.Empty<ForensicSignal>();
    }
}
