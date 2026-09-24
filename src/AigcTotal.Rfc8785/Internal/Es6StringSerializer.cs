using System;
using System.Globalization;
using System.Text;

namespace AigcTotal.Rfc8785.Internal
{
    /// <summary>
    /// RFC 8785 §3.2.2.2 字符串序列化（= ECMA-262 JSON.stringify 的转义规则）：
    /// U+0000–U+001F 中 \b \t \n \f \r 用短转义，其余用小写 \uhhhh；
    /// " 与 \ 转义；其余码点原样输出。孤立代理项 MUST 终止（§3.2.2.2 注）。
    /// </summary>
    internal static class Es6StringSerializer
    {
        public static void Append(StringBuilder sb, string value)
        {
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else if (char.IsHighSurrogate(c))
                        {
                            if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                            {
                                sb.Append(c).Append(value[i + 1]);
                                i++;
                            }
                            else
                            {
                                throw NotWellFormed(value);
                            }
                        }
                        else if (char.IsLowSurrogate(c))
                        {
                            throw NotWellFormed(value);
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

        /// <summary>UTF-16 码元序列合法性：每个高代理后必须跟低代理，低代理前必须是高代理。</summary>
        public static void EnsureWellFormed(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    {
                        throw NotWellFormed(value);
                    }

                    i++;
                }
                else if (char.IsLowSurrogate(c))
                {
                    throw NotWellFormed(value);
                }
            }
        }

        private static JsonCanonicalizationException NotWellFormed(string value)
        {
            return new JsonCanonicalizationException(
                "string contains a lone surrogate (not expressible as Unicode scalar values); " +
                "RFC 8785 §3.2.2.2 requires termination: " + Describe(value));
        }

        private static string Describe(string value)
        {
            var sb = new StringBuilder("U+");
            int shown = Math.Min(value.Length, 32);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(((int)value[i]).ToString("X4", CultureInfo.InvariantCulture));
            }

            if (value.Length > shown)
            {
                sb.Append(" …");
            }

            return sb.ToString();
        }
    }
}
