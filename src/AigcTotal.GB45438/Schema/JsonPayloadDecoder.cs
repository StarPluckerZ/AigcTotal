using System;
using System.Collections.Generic;
using System.Text;
using AigcTotal.GB45438.Carriers;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Schema
{
    /// <summary>
    /// 附录 E JSON 负载解码器：手写最小 JSON 扁平对象解析（纯 BCL，netstandard2.0 无 System.Text.Json）。
    /// 值捕获为原始文本：字符串解码转义，数字/字面量保留原文——回避 Label 值的数字/字符串形式未定问题。
    /// 嵌套对象/数组 → PayloadMalformed（宁降档）。
    /// </summary>
    public sealed class JsonPayloadDecoder : IPayloadDecoder
    {
        public PayloadEncoding Encoding => PayloadEncoding.Json;

        public PayloadDecodeResult Decode(byte[] payload)
        {
            return Decode(System.Text.Encoding.UTF8.GetString(payload));
        }

        public static PayloadDecodeResult Decode(string text)
        {
            try
            {
                var parser = new SimpleJsonParser(text);
                Dictionary<string, string> fields = parser.ParseFlatObject();
                return new PayloadDecodeResult(fields, null);
            }
            catch (FormatException)
            {
                return Fail(CheckCodes.PayloadMalformed);
            }
            catch (ArgumentNullException)
            {
                return Fail(CheckCodes.PayloadMalformed);
            }
            catch (ArgumentException)
            {
                return Fail(CheckCodes.PayloadMalformed);
            }
        }

        private static PayloadDecodeResult Fail(string code) => new PayloadDecodeResult(null, code);

        /// <summary>扁平 JSON 对象的最小解析器（对象 → 键值均为标量）。非线程安全，实例方法使用。</summary>
        private sealed class SimpleJsonParser
        {
            private readonly string _s;
            private int _i;

            public SimpleJsonParser(string text)
            {
                _s = text;
            }

            public Dictionary<string, string> ParseFlatObject()
            {
                var result = new Dictionary<string, string>();
                SkipWs();
                Expect('{');
                SkipWs();
                if (Peek() == '}')
                {
                    _i++;
                    return result;
                }
                while (true)
                {
                    SkipWs();
                    string key = ParseString();
                    SkipWs();
                    Expect(':');
                    SkipWs();
                    string value = ParseScalarValue();
                    result[key] = value;
                    SkipWs();
                    char c = Next();
                    if (c == '}') break;
                    if (c != ',') throw new FormatException("expected ',' or '}'");
                }
                return result;
            }

            private string ParseScalarValue()
            {
                char c = Peek();
                if (c == '"') return ParseString();
                if (c == '{' || c == '[')
                {
                    throw new FormatException("nested values are not allowed in label payloads");
                }
                // 数字 / true / false / null：捕获原始 token
                int start = _i;
                while (_i < _s.Length)
                {
                    char t = _s[_i];
                    if (t == ',' || t == '}' || t == ']' || t == ' ' || t == '\t' || t == '\r' || t == '\n') break;
                    _i++;
                }
                if (_i == start) throw new FormatException("empty value token");
                return _s.Substring(start, _i - start);
            }

            private string ParseString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw new FormatException("unterminated string");
                    char c = _s[_i++];
                    if (c == '"') break;
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
                                sb.Append((char)Convert.ToInt32(_s.Substring(_i, 4), 16));
                                _i += 4;
                                break;
                            default:
                                throw new FormatException("bad escape");
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
                return sb.ToString();
            }

            private char Peek()
            {
                if (_i >= _s.Length) throw new FormatException("unexpected end of input");
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
                char c = Next();
                if (c != expected) throw new FormatException($"expected '{expected}' got '{c}'");
            }

            private void SkipWs()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _i++;
                    else break;
                }
            }
        }
    }
}
