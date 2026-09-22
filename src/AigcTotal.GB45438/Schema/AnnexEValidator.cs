using System.Collections.Generic;
using AigcTotal.GB45438.Carriers;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Schema
{
    /// <summary>
    /// 附录 E 逐站点校验（基线规则；字符集与必填语义以标准原文为准绳，M1 对照 openstd 后收紧）。
    /// </summary>
    public static class AnnexEValidator
    {
        /// <summary>对一个站点的解码字段执行四项检查，产出站点级 check 结果。</summary>
        public static IEnumerable<CheckResult> Validate(LabelSite site, int siteIndex)
        {
            var fields = site.Fields;
            if (fields == null)
            {
                yield return new CheckResult(CheckIds.AnnexeFields, CheckOutcome.Error, site: siteIndex,
                    code: CheckCodes.PayloadMalformed, detail: "payload could not be decoded");
                yield break;
            }

            // 字段齐备性
            string? missing = FirstMissingRequired(fields);
            yield return missing == null
                ? new CheckResult(CheckIds.AnnexeFields, CheckOutcome.Pass, site: siteIndex)
                : new CheckResult(CheckIds.AnnexeFields, CheckOutcome.Fail, site: siteIndex,
                    code: CheckCodes.FieldMissing, detail: missing);

            // 字符集（GB 45438-2025 附录 E j)：值由 GB18030 码位 0x21、0x23~0x5B、0x5D~0x7E 的
            // 字符以及 \" 构成——解码后的值中允许 0x21..0x7E（含从 \" 转义来的引号），其余一律违规
            string? badCharset = FirstCharsetViolation(fields);
            yield return badCharset == null
                ? new CheckResult(CheckIds.AnnexeCharset, CheckOutcome.Pass, site: siteIndex)
                : new CheckResult(CheckIds.AnnexeCharset, CheckOutcome.Fail, site: siteIndex,
                    code: CheckCodes.CharsetInvalid, detail: badCharset);

            // Label 枚举（原始文本形式的 1/2/3）
            bool labelOk = fields.TryGetValue(AnnexEFields.Label, out string? label)
                && (label == "1" || label == "2" || label == "3");
            yield return labelOk
                ? new CheckResult(CheckIds.AnnexeLabelEnum, CheckOutcome.Pass, site: siteIndex)
                : new CheckResult(CheckIds.AnnexeLabelEnum, CheckOutcome.Fail, site: siteIndex,
                    code: CheckCodes.LabelEnumInvalid, detail: label ?? "<missing>");

            // 未知字段（严格策略）
            string? unknown = FirstUnknown(fields);
            yield return unknown == null
                ? new CheckResult(CheckIds.AnnexeUnknownField, CheckOutcome.Pass, site: siteIndex)
                : new CheckResult(CheckIds.AnnexeUnknownField, CheckOutcome.Fail, site: siteIndex,
                    code: CheckCodes.UnknownField, detail: unknown);
        }

        private static string? FirstMissingRequired(IReadOnlyDictionary<string, string> fields)
        {
            foreach (string name in AnnexEFields.RequiredNonEmpty)
            {
                if (!fields.TryGetValue(name, out string? value) || string.IsNullOrEmpty(value))
                {
                    return name;
                }
            }
            return null;
        }

        /// <summary>
        /// 附录 E j)：字段值限单字节可打印字符（0x21~0x7E，除 \" 转义外无引号/反斜杠/空格）。
        /// 解码后的 '"' 必然源自源文本 \" 转义（JSON 语法保证），允许；
        /// 解码后的 '\' 只能来自 \\ 等违规转义，拒绝；空格、控制字符、DEL、多字节（中文等）拒绝。
        /// </summary>
        private static string? FirstCharsetViolation(IReadOnlyDictionary<string, string> fields)
        {
            foreach (var pair in fields)
            {
                string value = pair.Value;
                foreach (char c in value)
                {
                    if (c == '"') continue;   // 源文本 \" 转义的产物，允许
                    if (c == '\\') return pair.Key;
                    if (c < 0x21 || c > 0x7E) return pair.Key;
                }
            }
            return null;
        }

        private static string? FirstUnknown(IReadOnlyDictionary<string, string> fields)
        {
            foreach (var key in fields.Keys)
            {
                if (!AnnexEFields.IsKnown(key)) return key;
            }
            return null;
        }
    }
}
