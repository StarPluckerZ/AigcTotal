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

            // 字符集基线：禁止控制字符
            string? badCharset = FirstControlChar(fields);
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

        private static string? FirstControlChar(IReadOnlyDictionary<string, string> fields)
        {
            foreach (var pair in fields)
            {
                foreach (char c in pair.Value)
                {
                    if (c < 0x20 || c == 0x7F)
                    {
                        return pair.Key;
                    }
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
