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
    /// 整数域 ±(2⁵³−1)（安全整数）：超域 long 会输出纯数字，而严格 JCS 实现（ES6 Number）
    /// 会输出科学计数法 → 跨实现哈希失配，故在序列化/反序列化两端强制拦截（schema §2 的护栏）。
    /// </summary>
    public static class CanonicalJson
    {
        /// <summary>I-JSON / ES6 安全整数边界 ±(2⁵³−1)。</summary>
        public const long MaxSafeInteger = (1L << 53) - 1;
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
                if (l > MaxSafeInteger || l < -MaxSafeInteger)
                {
                    throw new ArgumentException(
                        $"integer {l} exceeds the ±(2^53-1) safe range; strict JCS implementations would serialize it in scientific notation, breaking cross-implementation hashing");
                }
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

        /// <summary>
        /// canonical JSON 读端（只接受 <see cref="Serialize"/> 产出的子集）：顶层必须为对象；
        /// 值域 string / 整数 / bool / null / 数组 / 对象；浮点、重复键、尾随内容、孤立控制字符
        /// 一律 FormatException——与写端的类型防御对称。\uXXXX 转义按 JSON 逐字解码（含孤立代理项，
        /// 不做配对校验——数据透传原则）。
        /// </summary>
        public static Dictionary<string, object?> Deserialize(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var reader = new Reader(json);
            var result = reader.ReadObject();
            reader.SkipWs();
            if (!reader.Eof) throw new FormatException("trailing content after JSON object");
            return result;
        }

        private sealed class Reader
        {
            private readonly string _s;
            private int _i;

            public Reader(string s) { _s = s; }

            public bool Eof => _i >= _s.Length;

            public void SkipWs()
            {
                while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\r' || _s[_i] == '\n')) _i++;
            }

            public Dictionary<string, object?> ReadObject()
            {
                SkipWs();
                Expect('{');
                var result = new Dictionary<string, object?>();
                SkipWs();
                if (Peek() == '}')
                {
                    _i++;
                    return result;
                }
                while (true)
                {
                    SkipWs();
                    string key = ReadString();
                    if (result.ContainsKey(key)) throw new FormatException($"duplicate key '{key}'");
                    SkipWs();
                    Expect(':');
                    result[key] = ReadValue();
                    SkipWs();
                    char c = Next();
                    if (c == '}') return result;
                    if (c != ',') throw new FormatException("expected ',' or '}'");
                }
            }

            public object? ReadValue()
            {
                SkipWs();
                char c = Peek();
                switch (c)
                {
                    case '"': return ReadString();
                    case '{': return ReadObject();
                    case '[':
                        _i++;
                        var list = new List<object?>();
                        SkipWs();
                        if (Peek() == ']') { _i++; return list; }
                        while (true)
                        {
                            list.Add(ReadValue());
                            SkipWs();
                            char t = Next();
                            if (t == ']') return list;
                            if (t != ',') throw new FormatException("expected ',' or ']'");
                            SkipWs();
                        }
                    case 't':
                        ExpectLiteral("true"); return true;
                    case 'f':
                        ExpectLiteral("false"); return false;
                    case 'n':
                        ExpectLiteral("null"); return null;
                    default:
                        return ReadInteger();
                }
            }

            private long ReadInteger()
            {
                int start = _i;
                if (Peek() == '-') _i++;
                bool any = false;
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') { _i++; any = true; }
                if (!any) throw new FormatException("invalid number");
                if (_i < _s.Length && (_s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E'))
                {
                    throw new FormatException("floats are not allowed in canonical documents");
                }
#if NET
                bool ok = long.TryParse(_s.AsSpan(start, _i - start),
                    System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out long value);
#else
                bool ok = long.TryParse(_s.Substring(start, _i - start),
                    System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out long value);
#endif
                if (!ok)
                {
                    throw new FormatException("integer out of range");
                }
                if (value > MaxSafeInteger || value < -MaxSafeInteger)
                {
                    throw new FormatException("integer exceeds the ±(2^53-1) safe range");
                }
                return value;
            }

            private string ReadString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw new FormatException("unterminated string");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        if (_i >= _s.Length) throw new FormatException("unterminated escape");
                        char e = _s[_i++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (_i + 4 > _s.Length) throw new FormatException("bad \\u escape");
                                sb.Append((char)ParseHex4());
                                _i += 4;
                                break;
                            default: throw new FormatException("bad escape");
                        }
                    }
                    else if (c < 0x20)
                    {
                        throw new FormatException("raw control character in string");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }

            private int ParseHex4()
            {
                int value = 0;
                for (int k = 0; k < 4; k++)
                {
                    char c = _s[_i + k];
                    int d = c >= '0' && c <= '9' ? c - '0'
                        : c >= 'a' && c <= 'f' ? c - 'a' + 10
                        : c >= 'A' && c <= 'F' ? c - 'A' + 10
                        : throw new FormatException("bad hex digit in \\u escape");
                    value = (value << 4) | d;
                }
                return value;
            }

            private void ExpectLiteral(string literal)
            {
                if (_i + literal.Length > _s.Length ||
                    string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                {
                    throw new FormatException($"expected '{literal}'");
                }
                _i += literal.Length;
            }

            private char Peek()
            {
                if (Eof) throw new FormatException("unexpected end of input");
                return _s[_i];
            }

            private char Next()
            {
                char c = Peek();
                _i++;
                return c;
            }

            private void Expect(char expected)
            {
                if (Next() != expected) throw new FormatException($"expected '{expected}'");
            }
        }
    }
}
