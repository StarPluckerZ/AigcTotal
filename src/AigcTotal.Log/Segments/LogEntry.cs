using System;
using System.Collections.Generic;
using System.Globalization;
using AigcTotal.Report;

namespace AigcTotal.Log.Segments
{
    /// <summary>
    /// 透明日志条目（最小披露四字段：序号、时间、被检文件指纹、报告指纹）。
    /// 行 = canonical JSON，进 Merkle 叶。input_sha256 为被检文件整文件 SHA-256——
    /// 其他持有同文件的用户可按它定位条目（判定细节仍在报告本体，日志只披露"该文件曾被核查"）。
    /// </summary>
    public sealed record LogEntry(long Sequence, DateTimeOffset TimestampUtc, string InputSha256, string ReportSha256)
    {
        /// <summary>条目的 canonical JSON 行（键序 input_sha256 &lt; report_sha256 &lt; seq &lt; timestamp；LF 结尾由写侧负责）。</summary>
        public string ToCanonicalLine()
        {
            return CanonicalJson.Serialize(new Dictionary<string, object?>
            {
                ["input_sha256"] = InputSha256,
                ["report_sha256"] = ReportSha256,
                ["seq"] = Sequence,
                ["timestamp"] = LogTime.Format(TimestampUtc),
            });
        }
    }

    /// <summary>RFC 3339 UTC 秒精度时间（与报告信封同格式）；CLI/宿主的时间入参校验共用。</summary>
    public static class LogTime
    {
        private const string Pattern = "yyyy-MM-dd'T'HH:mm:ss'Z'";

        public static string Format(DateTimeOffset value) =>
            value.UtcDateTime.ToString(Pattern, CultureInfo.InvariantCulture);

        public static bool TryParse(string text, out DateTimeOffset value)
        {
            value = default;
            if (text == null || text.Length != 20 || text[text.Length - 1] != 'Z') return false;
            return DateTimeOffset.TryParseExact(text, Pattern, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
        }
    }

    /// <summary>报告指纹与 kid 的形态校验（写侧拒绝、读侧诊断共用）。</summary>
    internal static class TokenFormat
    {
        public static bool IsValidSha256Claim(string value)
        {
            if (value == null || value.Length != 7 + 64 || !value.StartsWith("sha256:", StringComparison.Ordinal))
            {
                return false;
            }
            for (int i = 7; i < value.Length; i++)
            {
                if (!IsLowerHex(value[i])) return false;
            }
            return true;
        }

        public static bool IsValidKid(string value)
        {
            if (value == null || value.Length != 1 + 64 || value[0] != 'k') return false;
            for (int i = 1; i < value.Length; i++)
            {
                if (!IsLowerHex(value[i])) return false;
            }
            return true;
        }

        private static bool IsLowerHex(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
    }
}
