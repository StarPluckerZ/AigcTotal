using System;
using System.Collections.Generic;
using System.Text;
using AigcTotal.GB45438.Carriers;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Schema
{
    /// <summary>
    /// Markdown front matter 的 AIGC YAML 映射解码器（TC260-PG-20258A）：
    /// 负载为 "AIGC:" 起始、缩进续行的 "Key: value" 行，值可为引号包裹——
    /// 指南示例即 'value 1' 形态。输出与 JSON 解码一致的七字段表，交附录 E 校验。
    /// </summary>
    public sealed class FrontMatterDecoder : IPayloadDecoder
    {
        public PayloadEncoding Encoding => PayloadEncoding.FrontMatterYaml;

        public PayloadDecodeResult Decode(byte[] payload)
        {
            var fields = new Dictionary<string, string>();
            // 属性名 Encoding 遮蔽类型，须全限定
            string text = System.Text.Encoding.UTF8.GetString(payload);
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0 || line.StartsWith("AIGC:", StringComparison.Ordinal)) continue;
                if (line[0] == '#') continue; // YAML 注释
                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    return new PayloadDecodeResult(null, CheckCodes.PayloadMalformed);
                }
                string key = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                // 只剥配对引号（'ab' → ab；"ab" → ab；不对称的引号是值的一部分，保留）
                if (value.Length >= 2
                    && ((value[0] == '"' && value[value.Length - 1] == '"')
                        || (value[0] == '\'' && value[value.Length - 1] == '\'')))
                {
                    value = value.Substring(1, value.Length - 2);
                }
                fields[key] = value;
            }
            if (fields.Count == 0)
            {
                return new PayloadDecodeResult(null, CheckCodes.PayloadMalformed);
            }
            return new PayloadDecodeResult(fields, null);
        }
    }
}
