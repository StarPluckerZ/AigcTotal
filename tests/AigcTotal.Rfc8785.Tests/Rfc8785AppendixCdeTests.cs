using System;
using System.Collections.Generic;
using Xunit;

namespace AigcTotal.Rfc8785.Tests
{
    /// <summary>RFC 8785 附录 C/D/E 正文样例的规范化行为。</summary>
    public class Rfc8785AppendixCdeTests
    {
        /// <summary>附录 C：canonical 形态会让地址记录按 address/city/name/state/zip 输出。</summary>
        [Fact]
        public void Canonicalize_appendix_c_address_record_sorts_properties()
        {
            const string input = """
                {
                  "name": "John Doe",
                  "address": "2000 Sunset Boulevard",
                  "city": "Los Angeles",
                  "zip": "90001",
                  "state": "CA"
                }
                """;

            const string expected = """
                {"address":"2000 Sunset Boulevard","city":"Los Angeles","name":"John Doe","state":"CA","zip":"90001"}
                """;

            Assert.Equal(expected, JsonCanonicalizer.Canonicalize(input));
        }

        /// <summary>附录 D：1.4e+9999 无法以 IEEE 754 binary64 表达（I-JSON），MUST 终止——
        /// 这正是附录 D 建议把此类数字改用字符串包装的原因。</summary>
        [Fact]
        public void Canonicalize_appendix_d_overflowing_number_terminates()
        {
            Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize(
                """
                {"giantNumber": 1.4e+9999,"payMeThis":26000.33,"int64Max":9223372036854775807}
                """));
        }

        /// <summary>附录 D 的可互操作形态：大数以字符串包装后规范化无障碍。</summary>
        [Fact]
        public void Canonicalize_appendix_d_string_wrapped_big_number_passes_through()
        {
            Assert.Equal(
                "{\"giantNumber\":\"1.4e+9999\"}",
                JsonCanonicalizer.Canonicalize("{\"giantNumber\": \"1.4e+9999\"}"));
        }

        /// <summary>附录 D：int64Max 按 ECMAScript 语义解析为最近的 binary64（2^63），序列化随之变形——
        /// 展示「JSON number 类型承载不了 int64」的附录 D 论点（V8 同样输出 9223372036854776000）。</summary>
        [Fact]
        public void Canonicalize_appendix_d_int64_max_rounds_to_binary64_semantics()
        {
            Assert.Equal(
                "{\"int64Max\":9223372036854776000}",
                JsonCanonicalizer.Canonicalize("{\"int64Max\": 9223372036854775807}"));
        }

        /// <summary>附录 E：纯字符串透传（不换子类型）时，规范化保持 "big":"055" 原样。</summary>
        [Fact]
        public void Canonicalize_appendix_e_pure_strings_are_preserved()
        {
            const string input = """
                {
                  "time": "2019-01-28T07:45:10Z",
                  "big": "055",
                  "val": 3.5
                }
                """;

            Assert.Equal(
                "{\"big\":\"055\",\"time\":\"2019-01-28T07:45:10Z\",\"val\":3.5}",
                JsonCanonicalizer.Canonicalize(input));
        }

        /// <summary>附录 E 的反面论证：若解析期把子类型换成 Date/BigInt（toJSON 语义），字符串内容会漂移。
        /// 本库的 DOM 不承载子类型（string 即 string），结构上杜绝该漂移；此处以等价的「程序化 DOM」固定该行为。</summary>
        [Fact]
        public void Serialize_appendix_e_dom_values_map_one_to_one_without_drift()
        {
            var document = new Dictionary<string, object?>
            {
                ["time"] = "2019-01-28T07:45:10Z",
                ["big"] = "055",
                ["val"] = 3.5d,
            };

            Assert.Equal(
                "{\"big\":\"055\",\"time\":\"2019-01-28T07:45:10Z\",\"val\":3.5}",
                JsonCanonicalizer.Serialize(document));
        }
    }
}
