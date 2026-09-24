using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AigcTotal.Rfc8785.Internal
{
    /// <summary>
    /// 严格 RFC 8259 JSON 解析器，产出 I-JSON（RFC 7493）约束下的 DOM：
    /// 所有数字解析为 IEEE 754 binary64（ECMAScript JSON.parse 语义，§3.1）、
    /// 拒绝重复属性名、拒绝孤立代理项、拒绝尾随内容与原始控制字符。
    /// 值类型：null / bool / double / string / List&lt;object?&gt; / Dictionary&lt;string, object?&gt;。
    /// </summary>
    internal sealed class JsonParser
    {
        /// <summary>深度上限（RFC 8785 §5：对输入做健全性检查，防栈溢出）。</summary>
        public const int MaxDepth = 1000;

        private readonly string _text;
        private int _pos;

        private JsonParser(string text)
        {
            _text = text;
        }

        public static object? Parse(string text)
        {
            var parser = new JsonParser(text);
            object? value = parser.ParseValue(0);
            parser.SkipWhitespace();
            if (!parser.AtEnd)
            {
                throw new JsonCanonicalizationException(
                    $"unexpected trailing content at offset {parser._pos} after the top-level JSON value");
            }

            return value;
        }

        private bool AtEnd => _pos >= _text.Length;

        private void SkipWhitespace()
        {
            while (_pos < _text.Length)
            {
                char c = _text[_pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                {
                    _pos++;
                }
                else
                {
                    break;
                }
            }
        }

        private object? ParseValue(int depth)
        {
            if (depth > MaxDepth)
            {
                throw new JsonCanonicalizationException($"JSON nesting deeper than {MaxDepth} levels");
            }

            SkipWhitespace();
            if (AtEnd)
            {
                throw new JsonCanonicalizationException("unexpected end of input");
            }

            char c = _text[_pos];
            switch (c)
            {
                case '{':
                    return ParseObject(depth);
                case '[':
                    return ParseArray(depth);
                case '"':
                    return ParseString();
                case 't':
                    return ParseLiteral("true", true);
                case 'f':
                    return ParseLiteral("false", false);
                case 'n':
                    return ParseLiteral("null", null);
                default:
                    return ParseNumber();
            }
        }

        private object? ParseLiteral(string literal, object? value)
        {
            if (_pos + literal.Length > _text.Length ||
                string.CompareOrdinal(_text, _pos, literal, 0, literal.Length) != 0)
            {
                throw new JsonCanonicalizationException($"invalid literal at offset {_pos} (expected '{literal}')");
            }

            _pos += literal.Length;
            return value;
        }

        private Dictionary<string, object?> ParseObject(int depth)
        {
            _pos++; // '{'
            var result = new Dictionary<string, object?>();
            SkipWhitespace();
            if (!AtEnd && _text[_pos] == '}')
            {
                _pos++;
                return result;
            }

            while (true)
            {
                SkipWhitespace();
                if (AtEnd || _text[_pos] != '"')
                {
                    throw new JsonCanonicalizationException($"expected property name at offset {_pos}");
                }

                string name = ParseString();
                if (result.ContainsKey(name))
                {
                    throw new JsonCanonicalizationException(
                        "duplicate property name (violates I-JSON, RFC 8785 §3.1): " + name);
                }

                SkipWhitespace();
                if (AtEnd || _text[_pos] != ':')
                {
                    throw new JsonCanonicalizationException($"expected ':' at offset {_pos}");
                }

                _pos++;
                result[name] = ParseValue(depth + 1);
                SkipWhitespace();
                if (AtEnd)
                {
                    throw new JsonCanonicalizationException("unterminated JSON object");
                }

                char c = _text[_pos++];
                if (c == '}')
                {
                    return result;
                }

                if (c != ',')
                {
                    throw new JsonCanonicalizationException($"expected ',' or '}}' at offset {_pos - 1}");
                }
            }
        }

        private List<object?> ParseArray(int depth)
        {
            _pos++; // '['
            var result = new List<object?>();
            SkipWhitespace();
            if (!AtEnd && _text[_pos] == ']')
            {
                _pos++;
                return result;
            }

            while (true)
            {
                result.Add(ParseValue(depth + 1));
                SkipWhitespace();
                if (AtEnd)
                {
                    throw new JsonCanonicalizationException("unterminated JSON array");
                }

                char c = _text[_pos++];
                if (c == ']')
                {
                    return result;
                }

                if (c != ',')
                {
                    throw new JsonCanonicalizationException($"expected ',' or ']' at offset {_pos - 1}");
                }
            }
        }

        private string ParseString()
        {
            _pos++; // '"'
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw new JsonCanonicalizationException("unterminated JSON string");
                }

                char c = _text[_pos++];
                if (c == '"')
                {
                    string result = sb.ToString();
                    Es6StringSerializer.EnsureWellFormed(result);
                    return result;
                }

                if (c == '\\')
                {
                    ReadEscape(sb);
                }
                else if (c < 0x20)
                {
                    throw new JsonCanonicalizationException(
                        $"raw control character U+{(int)c:X4} in JSON string at offset {_pos - 1}");
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        private void ReadEscape(StringBuilder sb)
        {
            if (AtEnd)
            {
                throw new JsonCanonicalizationException("unterminated escape sequence");
            }

            char e = _text[_pos++];
            switch (e)
            {
                case '"':
                    sb.Append('"');
                    break;
                case '\\':
                    sb.Append('\\');
                    break;
                case '/':
                    sb.Append('/');
                    break;
                case 'b':
                    sb.Append('\b');
                    break;
                case 'f':
                    sb.Append('\f');
                    break;
                case 'n':
                    sb.Append('\n');
                    break;
                case 'r':
                    sb.Append('\r');
                    break;
                case 't':
                    sb.Append('\t');
                    break;
                case 'u':
                    sb.Append((char)ReadHex4());
                    break;
                default:
                    throw new JsonCanonicalizationException($"invalid escape '\\{e}' at offset {_pos - 2}");
            }
        }

        private int ReadHex4()
        {
            if (_pos + 4 > _text.Length)
            {
                throw new JsonCanonicalizationException("truncated \\u escape");
            }

            int value = 0;
            for (int i = 0; i < 4; i++)
            {
                char c = _text[_pos + i];
                int digit = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'a' and <= 'f' => c - 'a' + 10,
                    >= 'A' and <= 'F' => c - 'A' + 10,
                    _ => throw new JsonCanonicalizationException(
                        $"invalid hex digit in \\u escape at offset {_pos + i}"),
                };
                value = (value << 4) | digit;
            }

            _pos += 4;
            return value;
        }

        private double ParseNumber()
        {
            int start = _pos;
            if (!AtEnd && _text[_pos] == '-')
            {
                _pos++;
            }

            if (AtEnd)
            {
                throw new JsonCanonicalizationException("truncated JSON number");
            }

            if (_text[_pos] == '0')
            {
                _pos++;
                if (!AtEnd && _text[_pos] >= '0' && _text[_pos] <= '9')
                {
                    throw new JsonCanonicalizationException(
                        $"leading zero in JSON number at offset {start} (RFC 8259 grammar)");
                }
            }
            else
            {
                ReadDigits();
            }

            if (!AtEnd && _text[_pos] == '.')
            {
                _pos++;
                ReadDigits();
            }

            if (!AtEnd && (_text[_pos] == 'e' || _text[_pos] == 'E'))
            {
                _pos++;
                if (!AtEnd && (_text[_pos] == '+' || _text[_pos] == '-'))
                {
                    _pos++;
                }

                ReadDigits();
            }

            string slice = _text.Substring(start, _pos - start);
            if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new JsonCanonicalizationException(
                    $"JSON number '{slice}' is not expressible as an IEEE 754 double (I-JSON, RFC 8785 §3.1)");
            }

            return value;
        }

        private void ReadDigits()
        {
            int begin = _pos;
            while (!AtEnd && _text[_pos] >= '0' && _text[_pos] <= '9')
            {
                _pos++;
            }

            if (_pos == begin)
            {
                throw new JsonCanonicalizationException($"missing digits in JSON number at offset {begin}");
            }
        }
    }
}
