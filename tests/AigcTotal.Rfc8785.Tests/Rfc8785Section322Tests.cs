using System;
using System.Text;
using Xunit;

namespace AigcTotal.Rfc8785.Tests
{
    /// <summary>
    /// RFC 8785 §3.2.2（原语序列化）与 §3.2.3/§3.2.4（键排序 + UTF-8 生成）的组合向量：
    /// 输入 JSON、排序后规范形态、以及 §3.2.4 给出的逐字节 UTF-8 十六进制。
    /// </summary>
    public class Rfc8785Section322Tests
    {
        private const string Rfc322Input = """
            {
              "numbers": [333333333.33333329, 1E30, 4.50,
                          2e-3, 0.000000000000000000000000001],
              "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
              "literals": [null, true, false]
            }
            """;

        private const string Rfc322Canonical = """
            {"literals":[null,true,false],"numbers":[333333333.3333333,1e+30,4.5,0.002,1e-27],"string":"€$\u000f\nA'B\"\\\\\"/"}
            """;

        /// <summary>RFC 8785 §3.2.4 的字节级向量（十六进制）。</summary>
        private const string Rfc324Utf8Hex =
            "7b 22 6c 69 74 65 72 61 6c 73 22 3a 5b 6e 75 6c 6c 2c 74 72 75 65 2c 66 61 6c 73 65 5d 2c 22 6e 75 6d 62 65 72 73 22 3a " +
            "5b 33 33 33 33 33 33 33 33 33 2e 33 33 33 33 33 33 33 2c 31 65 2b 33 30 2c 34 2e 35 2c 30 2e 30 30 32 2c 31 65 2d 32 37 " +
            "5d 2c 22 73 74 72 69 6e 67 22 3a 22 e2 82 ac 24 5c 75 30 30 30 66 5c 6e 41 27 42 5c 22 5c 5c 5c 5c 5c 22 2f 22 7d";

        [Fact]
        public void Canonicalize_section322_sample_matches_canonical_form()
        {
            Assert.Equal(Rfc322Canonical, JsonCanonicalizer.Canonicalize(Rfc322Input));
        }

        [Fact]
        public void CanonicalizeToUtf8_section324_hex_vector_matches_byte_for_byte()
        {
            byte[] expected = Convert.FromHexString(Rfc324Utf8Hex.Replace(" ", string.Empty, StringComparison.Ordinal));
            byte[] actual = JsonCanonicalizer.CanonicalizeToUtf8(Rfc322Input);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void Canonicalize_escaped_solidus_and_case_insensitive_escapes_are_normalized()
        {
            // RFC 输入含 \/ 与 \u000F/\u000a（大小写两种十六进制写法），输出统一为小写短转义
            Assert.Equal("\"/€$\\u000f\\n\"", JsonCanonicalizer.Canonicalize("\"\\/\\u20ac$\\u000F\\u000A\""));
        }
    }

    /// <summary>RFC 8785 §3.2.3：UTF-16 码元序递归排序向量。</summary>
    public class Rfc8785Section323Tests
    {
        private const string Rfc323Input = """
            {
              "\u20ac": "Euro Sign",
              "\r": "Carriage Return",
              "\ufb33": "Hebrew Letter Dalet With Dagesh",
              "1": "One",
              "\ud83d\ude00": "Emoji: Grinning Face",
              "\u0080": "Control",
              "\u00f6": "Latin Small Letter O With Diaeresis"
            }
            """;

        // U+0080 不在 U+0000–U+001F 控制区间内，按 §3.2.2.2「as is」原样输出（与官方 weird.json 一致）
        private const string Rfc323Canonical =
            "{\"\\r\":\"Carriage Return\",\"1\":\"One\",\"" + "\u0080" +
            "\":\"Control\",\"\u00f6\":\"Latin Small Letter O With Diaeresis\",\"\u20ac\":\"Euro Sign\"," +
            "\"\U0001F600\":\"Emoji: Grinning Face\",\"\uFB33\":\"Hebrew Letter Dalet With Dagesh\"}";

        /// <summary>RFC 8785 §3.2.3「Expected argument order after sorting property strings」。</summary>
        private static readonly string[] ExpectedArgumentOrder =
        {
            "Carriage Return",
            "One",
            "Control",
            "Latin Small Letter O With Diaeresis",
            "Euro Sign",
            "Emoji: Grinning Face",
            "Hebrew Letter Dalet With Dagesh",
        };

        [Fact]
        public void Canonicalize_section323_sorting_sample_matches_canonical_form()
        {
            Assert.Equal(Rfc323Canonical, JsonCanonicalizer.Canonicalize(Rfc323Input));
        }

        [Fact]
        public void Canonicalize_property_order_matches_the_rfc_argument_order()
        {
            string canonical = JsonCanonicalizer.Canonicalize(Rfc323Input);
            int previous = -1;
            foreach (string label in ExpectedArgumentOrder)
            {
                int index = canonical.IndexOf(label, StringComparison.Ordinal);
                Assert.True(index > previous, $"property '{label}' appears out of the RFC §3.2.3 argument order");
                previous = index;
            }
        }
    }
}
