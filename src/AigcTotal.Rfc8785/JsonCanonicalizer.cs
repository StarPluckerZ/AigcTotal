using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AigcTotal.Rfc8785
{
    /// <summary>
    /// RFC 8785 JSON Canonicalization Scheme (JCS) 完整实现：
    /// 严格 I-JSON 解析（数字一律 binary64）→ 原语按 ECMAScript 序列化 →
    /// 对象键按 UTF-16 码元序（Ordinal）递归排序 → 无空白 UTF-8 输出。
    /// 输入不满足前提（NaN/Infinity、超出 binary64、孤立代理项、重复键、非法语法）即抛
    /// <see cref="JsonCanonicalizationException"/>。
    /// </summary>
    public static class JsonCanonicalizer
    {
        /// <summary>DOM 遍历与 JSON 嵌套共用的深度上限（RFC 8785 §5 健全性检查）。</summary>
        public const int MaxDepth = Internal.JsonParser.MaxDepth;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>解析 JSON 文本并返回规范化文本（无空白、键有序）。</summary>
        public static string Canonicalize(string json)
        {
            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            if (json.Length > 0 && json[0] == '\uFEFF')
            {
                json = json.Substring(1); // UTF-8/UTF-16 解码器残留的 BOM 签名，非 JSON 内容（RFC 8259 §8.1 允许忽略）
            }

            return Serialize(Internal.JsonParser.Parse(json));
        }

        /// <summary>解析 JSON 文本并返回规范化 UTF-8 字节（可直接用于哈希/签名，§3.2.4）。</summary>
        public static byte[] CanonicalizeToUtf8(string json)
        {
            return Encoding.UTF8.GetBytes(Canonicalize(json));
        }

        /// <summary>解码严格 UTF-8 字节后规范化，返回规范化 UTF-8 字节。非法 UTF-8 即报错（不做 U+FFFD 替换）。</summary>
        public static byte[] CanonicalizeToUtf8(byte[] utf8Json)
        {
            if (utf8Json is null)
            {
                throw new ArgumentNullException(nameof(utf8Json));
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(utf8Json);
            }
            catch (DecoderFallbackException ex)
            {
                throw new JsonCanonicalizationException("input is not well-formed UTF-8", ex);
            }

            return CanonicalizeToUtf8(text);
        }

        /// <summary>
        /// 对程序化构造的 DOM 直接规范化。值类型：null / bool / double / long / int /
        /// byte / sbyte / short / ushort / uint / string / List&lt;object?&gt; / Dictionary&lt;string, object?&gt;。
        /// 整型按 ECMAScript Number 语义转为 binary64（≥ 2^53 时丢精度，与 JS 一致）；
        /// float / decimal / ulong 无 ECMAScript 对应物，明确拒绝。
        /// </summary>
        public static string Serialize(object? value)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        /// <summary><see cref="Serialize"/> 的 UTF-8 字节版（§3.2.4）。</summary>
        public static byte[] SerializeToUtf8(object? value)
        {
            return Encoding.UTF8.GetBytes(Serialize(value));
        }

        private static void WriteValue(StringBuilder sb, object? value, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new JsonCanonicalizationException($"document nesting deeper than {MaxDepth} levels");
            }

            if (value is null)
            {
                sb.Append("null");
                return;
            }

            if (value is bool b)
            {
                sb.Append(b ? "true" : "false");
                return;
            }

            if (value is string s)
            {
                Internal.Es6StringSerializer.Append(sb, s);
                return;
            }

            if (value is double d)
            {
                sb.Append(Internal.Es6NumberSerializer.Serialize(d));
                return;
            }

            if (value is long || value is int || value is byte || value is sbyte
                || value is short || value is ushort || value is uint)
            {
                double exact = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                sb.Append(Internal.Es6NumberSerializer.Serialize(exact));
                return;
            }

            if (value is List<object?> list)
            {
                WriteArray(sb, list, depth);
                return;
            }

            if (value is Dictionary<string, object?> dict)
            {
                WriteObject(sb, dict, depth);
                return;
            }

            if (value is float || value is decimal || value is ulong)
            {
                throw new ArgumentException(
                    $"type {value.GetType().Name} has no ECMAScript Number counterpart; " +
                    "use double (IEEE 754 binary64 per I-JSON) or long within ±2^53");
            }

            throw new ArgumentException($"unsupported canonical value type {value.GetType().Name}");
        }

        private static void WriteArray(StringBuilder sb, List<object?> list, int depth)
        {
            sb.Append('[');
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                WriteValue(sb, list[i], depth + 1);
            }

            sb.Append(']');
        }

        private static void WriteObject(StringBuilder sb, Dictionary<string, object?> dict, int depth)
        {
            sb.Append('{');
            // JCS §3.2.3：键按 UTF-16 码元序排序（Ordinal == 码元逐位比较），递归作用于全部子对象
            var keys = new string[dict.Count];
            dict.Keys.CopyTo(keys, 0);
            Array.Sort(keys, StringComparer.Ordinal);
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                Internal.Es6StringSerializer.Append(sb, keys[i]);
                sb.Append(':');
                WriteValue(sb, dict[keys[i]], depth + 1);
            }

            sb.Append('}');
        }
    }
}
