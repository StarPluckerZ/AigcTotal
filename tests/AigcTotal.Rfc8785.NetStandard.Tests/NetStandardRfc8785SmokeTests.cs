using System;
using System.Text;
using Xunit;

namespace AigcTotal.Rfc8785.NetStandard.Tests
{
    /// <summary>
    /// netstandard2.0 编译产物行为冒烟：确认 ES6 数字序列化（BigInteger 精确算法，
    /// 不依赖运行时格式化）、字符串转义与排序在 ns2.0 目标上与 net10.0 输出逐字节一致。
    /// </summary>
    public class NetStandardRfc8785SmokeTests
    {
        [Fact]
        public void Canonicalize_official_values_sample_is_byte_identical()
        {
            const string input = """
                {
                  "numbers": [333333333.33333329, 1E30, 4.50,
                              2e-3, 0.000000000000000000000000001],
                  "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
                  "literals": [null, true, false]
                }
                """;

            const string expected = """
                {"literals":[null,true,false],"numbers":[333333333.3333333,1e+30,4.5,0.002,1e-27],"string":"€$\u000f\nA'B\"\\\\\"/"}
                """;

            Assert.Equal(expected, JsonCanonicalizer.Canonicalize(input));
        }

        [Theory]
        [InlineData("0000000000000001", "5e-324")]
        [InlineData("4430000000000000", "295147905179352830000")]
        [InlineData("44b52d02c7e14af5", "9.999999999999997e+22")]
        [InlineData("444b1ae4d6e2ef50", "1e+21")]
        [InlineData("43143ff3c1cb0959", "1424953923781206.2")]
        public void Serialize_appendix_b_edge_rows_match_on_ns20(string hex, string expected)
        {
            double value = BitConverter.Int64BitsToDouble(unchecked((long)Convert.ToUInt64(hex, 16)));
            Assert.Equal(expected, JsonCanonicalizer.Serialize(value));
        }

        [Fact]
        public void Serialize_sorts_keys_by_utf16_code_units_on_ns20()
        {
            var document = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["z"] = 1,
                ["😀"] = 2,
                ["Z"] = 3,
            };

            Assert.Equal("{\"Z\":3,\"z\":1,\"😀\":2}", JsonCanonicalizer.Serialize(document));
        }

        [Fact]
        public void CanonicalizeToUtf8_emits_bom_free_utf8_on_ns20()
        {
            byte[] utf8 = JsonCanonicalizer.CanonicalizeToUtf8("{\"€\":1}");
            Assert.Equal("7B22E282AC223A317D", Convert.ToHexString(utf8));
        }
    }
}
