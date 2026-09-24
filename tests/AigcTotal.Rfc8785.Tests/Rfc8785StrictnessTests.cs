using System;
using Xunit;

namespace AigcTotal.Rfc8785.Tests
{
    /// <summary>
    /// RFC 8785 §3.1 / §3.2.2.2 注 / §3.2.2.3 注 / §5 规定的终止义务，
    /// 以及 RFC 8259 语法严格性（JCS 输入必须是可解析的合法 JSON）。
    /// </summary>
    public class Rfc8785StrictnessTests
    {
        [Theory]
        [InlineData("{\"a\":1,\"a\":2}")]           // 重复属性名（I-JSON）
        [InlineData("{\"a\":\"\\u0061\",\"\\u0061\":2}")] // 转义写法不同的同名属性
        public void Canonicalize_rejects_duplicate_property_names(string input)
        {
            var ex = Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize(input));
            Assert.Contains("duplicate property name", ex.Message);
        }

        [Theory]
        [InlineData("\"\\ud800\"")]                 // 孤立高代理（转义引入）
        [InlineData("\"\\udc00\"")]                 // 孤立低代理
        [InlineData("\"\\ud83d\"")]                 // 高代理后无低代理
        [InlineData("\"x\\ude00y\"")]               // 低代理前无高代理
        public void Canonicalize_rejects_lone_surrogates(string escapedInput)
        {
            Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize(escapedInput));
        }

        [Fact]
        public void Canonicalize_accepts_well_formed_surrogate_pairs()
        {
            Assert.Equal("\"😀\"", JsonCanonicalizer.Canonicalize("\"\\ud83d\\ude00\""));
        }

        [Fact]
        public void Canonicalize_rejects_raw_lone_surrogate_in_text()
        {
            // 源文本中的原始孤立代理（U+D800）同样必须终止
            Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize("\"\ud800\""));
        }

        [Theory]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("-Infinity")]
        [InlineData("1e999")]                       // 溢出 → 非 IEEE 754 可表达
        [InlineData("-1e999")]
        public void Canonicalize_rejects_non_representable_numbers(string input)
        {
            Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize(input));
        }

        [Theory]
        [InlineData("{\"a\":NaN}")]                 // NaN 不是合法 JSON
        [InlineData("{'a':1}")]                     // 单引号不是 JSON
        [InlineData("[1,]")]                        // 尾随逗号
        [InlineData("{\"a\":1}x")]                  // 尾随内容
        [InlineData("01")]                          // 前导零
        [InlineData("1.")]                          // 小数点后无数字
        [InlineData(".5")]                          // 整数部分缺失
        [InlineData("+1")]                          // 正号
        [InlineData("\"\\x41\"")]                   // 非法转义
        [InlineData("\"\\u12g4\"")]                 // 非法十六进制
        [InlineData("\"a\u000bb\"")]                // 原始控制字符
        [InlineData("[1 2]")]                       // 缺逗号
        [InlineData("{\"\u0061\":1,}")]             // 对象尾随逗号
        [InlineData("")]                            // 空输入
        [InlineData("nullx")]
        public void Canonicalize_rejects_invalid_json_grammar(string input)
        {
            Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize(input));
        }

        [Fact]
        public void Canonicalize_rejects_input_beyond_max_depth()
        {
            Assert.Throws<JsonCanonicalizationException>(() =>
                JsonCanonicalizer.Canonicalize("[" + new string('[', 2000) + new string(']', 2000) + "]"));
        }

        [Fact]
        public void CanonicalizeToUtf8_rejects_malformed_utf8_without_replacement()
        {
            byte[] malformed = { 0x22, 0xE2, 0x82 }; // " 后截断的 € 序列
            Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.CanonicalizeToUtf8(malformed));
        }

        [Fact]
        public void CanonicalizeToUtf8_accepts_utf8_and_produces_utf8()
        {
            byte[] input = { 0x7B, 0x22, 0xE2, 0x82, 0xAC, 0x22, 0x3A, 0x31, 0x7D }; // {"€":1}
            byte[] actual = JsonCanonicalizer.CanonicalizeToUtf8(input);
            Assert.Equal("7B22E282AC223A317D", Convert.ToHexString(actual));
        }

        [Fact]
        public void Canonicalize_skips_decoded_bom_signature()
        {
            Assert.Equal("1", JsonCanonicalizer.Canonicalize("\uFEFF1"));
            Assert.Equal("31", Convert.ToHexString(JsonCanonicalizer.CanonicalizeToUtf8(new byte[] { 0xEF, 0xBB, 0xBF, 0x31 })));
        }

        [Fact]
        public void Serialize_rejects_types_without_ecmascript_counterpart()
        {
            Assert.Throws<ArgumentException>(() => JsonCanonicalizer.Serialize(0.5f));
            Assert.Throws<ArgumentException>(() => JsonCanonicalizer.Serialize(0.5m));
            Assert.Throws<ArgumentException>(() => JsonCanonicalizer.Serialize(1ul));
        }

        [Fact]
        public void Serialize_rejects_nan_and_infinity_doubles()
        {
            Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Serialize(double.NaN));
            Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Serialize(double.PositiveInfinity));
        }

        [Fact]
        public void Serialize_converts_integral_types_via_binary64_like_ecmascript_number()
        {
            Assert.Equal("56", JsonCanonicalizer.Serialize(56));
            Assert.Equal("56", JsonCanonicalizer.Serialize(56L));
            Assert.Equal("9007199254740992", JsonCanonicalizer.Serialize(9007199254740992L));
            // 2^53+1 精度丢失与 JS Number(9007199254740993) 一致 → 9007199254740992
            Assert.Equal("9007199254740992", JsonCanonicalizer.Serialize(9007199254740993L));
        }

        [Fact]
        public void Serialize_sorts_keys_recursively_by_utf16_code_units()
        {
            var document = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["b"] = new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["z"] = 1,
                    ["\ud83d\ude00"] = 2,
                    ["Z"] = 3,
                },
                ["a"] = new System.Collections.Generic.List<object?> { 4, new System.Collections.Generic.Dictionary<string, object?> { ["y"] = 5 } },
                [""] = 6,
            };

            // 码元序：'Z'(0x5A) < 'z'(0x7A) < 高代理 0xD83D（😀）
            Assert.Equal(
                "{\"\":6,\"a\":[4,{\"y\":5}],\"b\":{\"Z\":3,\"z\":1,\"\U0001F600\":2}}",
                JsonCanonicalizer.Serialize(document));
        }

        [Fact]
        public void Canonicalize_passes_unnormalized_unicode_through_without_normalization()
        {
            // RFC 8785 §3.1 注：不做 Unicode Normalization；官方 testdata unicode.json 同款
            Assert.Equal(
                "{\"Unnormalized Unicode\":\"A\u030A\"}",
                JsonCanonicalizer.Canonicalize("{\"Unnormalized Unicode\":\"A\u030A\"}"));
        }
    }
}
