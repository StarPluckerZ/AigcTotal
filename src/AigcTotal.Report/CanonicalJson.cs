using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AigcTotal.Report
{
    /// <summary>
    /// Canonical JSON 序列化器（RFC 8785 JCS 的受限子集，见 docs/canonicalization.md）：
    /// 键按序数（UTF-16 码元）排序、无空白、最短字符串转义。
    /// 值域仅允许：null / bool / long / int / string / List&lt;object?&gt; / Dictionary&lt;string, object?&gt;——
    /// 信封 schema 禁浮点，序列化器对浮点直接抛异常（防御）。
    /// </summary>
    public static class CanonicalJson
    {
        public static string Serialize(Dictionary<string, object?> document)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, document, 0, pretty: false);
            return sb.ToString();
        }

        /// <summary>人类可读输出（保留排序，增加缩进；非 canonical，仅供阅读，不可用于哈希/验证）。</summary>
        public static string SerializePretty(Dictionary<string, object?> document)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, document, 0, pretty: true);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object? value, int depth, bool pretty)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }
            if (value is bool b)
            {
                sb.Append(b ? "true" : "false");
                return;
            }
            if (value is long l)
            {
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (value is int i)
            {
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (value is string s)
            {
                WriteString(sb, s);
                return;
            }
            if (value is List<object?> list)
            {
                WriteArray(sb, list, depth, pretty);
                return;
            }
            if (value is Dictionary<string, object?> dict)
            {
                WriteObject(sb, dict, depth, pretty);
                return;
            }
            if (value is double || value is float || value is decimal || value is uint || value is short
                || value is byte || value is sbyte || value is ushort || value is ulong)
            {
                throw new ArgumentException(
                    $"type {value.GetType().Name} is not allowed in canonical documents (integers only, use long)");
            }
            throw new ArgumentException($"unsupported canonical value type {value.GetType().Name}");
        }

        private static void WriteObject(StringBuilder sb, Dictionary<string, object?> dict, int depth, bool pretty)
        {
            if (dict.Count == 0)
            {
                sb.Append("{}");
                return;
            }
            // JCS：键按 UTF-16 码元序（Ordinal）
            var keys = new string[dict.Count];
            dict.Keys.CopyTo(keys, 0);
            Array.Sort(keys, StringComparer.Ordinal);

            sb.Append('{');
            for (int k = 0; k < keys.Length; k++)
            {
                if (k > 0) sb.Append(',');
                if (pretty) sb.Append('\n').Append(' ', (depth + 1) * 2);
                WriteString(sb, keys[k]);
                sb.Append(':');
                if (pretty) sb.Append(' ');
                WriteValue(sb, dict[keys[k]], depth + 1, pretty);
            }
            if (pretty) sb.Append('\n').Append(' ', depth * 2);
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, List<object?> list, int depth, bool pretty)
        {
            if (list.Count == 0)
            {
                sb.Append("[]");
                return;
            }
            sb.Append('[');
            for (int k = 0; k < list.Count; k++)
            {
                if (k > 0) sb.Append(',');
                if (pretty) sb.Append('\n').Append(' ', (depth + 1) * 2);
                WriteValue(sb, list[k], depth + 1, pretty);
            }
            if (pretty) sb.Append('\n').Append(' ', depth * 2);
            sb.Append(']');
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else if (char.IsSurrogate(c))
                        {
                            // RFC 8785 / ES6 JSON.stringify：合法代理对原样透传（编码为 4 字节 UTF-8）；
                            // 孤立代理项转义为 \udXXX——原样输出会在 UTF-8 编码时被替换成 U+FFFD，
                            // 破坏跨实现的字节可复现性
                            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                            {
                                sb.Append(c).Append(value[i + 1]);
                                i++;
                            }
                            else
                            {
                                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
