using System;
using System.Collections.Generic;
using Xunit;

namespace AigcTotal.Report.Tests
{
    public class CanonicalJsonTests
    {
        [Fact]
        public void Keys_SortedByUtf16CodeUnits()
        {
            var doc = new Dictionary<string, object?>
            {
                ["b"] = 1L,
                ["a"] = 2L,
                ["A"] = 3L, // 'A'(65) < 'a'(97) < 'b'(98)
            };

            Assert.Equal("{\"A\":3,\"a\":2,\"b\":1}", CanonicalJson.Serialize(doc));
        }

        [Fact]
        public void Nested_Structures_And_Types()
        {
            var doc = new Dictionary<string, object?>
            {
                ["verdict"] = "compliant",
                ["count"] = 42L,
                ["ok"] = true,
                ["nothing"] = (object?)null,
                ["list"] = new List<object?> { 1L, "x", new Dictionary<string, object?> { ["inner"] = false } },
            };

            Assert.Equal(
                "{\"count\":42,\"list\":[1,\"x\",{\"inner\":false}],\"nothing\":null,\"ok\":true,\"verdict\":\"compliant\"}",
                CanonicalJson.Serialize(doc));
        }

        [Fact]
        public void StringEscaping_MinimalForm()
        {
            var doc = new Dictionary<string, object?>
            {
                ["quote"] = "a\"b",
                ["backslash"] = "a\\b",
                ["newline"] = "a\nb",
                ["control"] = "a\u0001b",
                ["chinese"] = "中文内容",
            };

            Assert.Equal(
                "{\"backslash\":\"a\\\\b\",\"chinese\":\"中文内容\",\"control\":\"a\\u0001b\",\"newline\":\"a\\nb\",\"quote\":\"a\\\"b\"}",
                CanonicalJson.Serialize(doc));
        }

        [Fact]
        public void Floats_Are_Rejected_SchemaDefense()
        {
            var doc = new Dictionary<string, object?> { ["bad"] = 1.5 };
            Assert.Throws<ArgumentException>(() => CanonicalJson.Serialize(doc));
        }

        [Fact]
        public void Pretty_KeepsOrder_AddsWhitespace()
        {
            var doc = new Dictionary<string, object?> { ["b"] = 1L, ["a"] = 2L };
            string pretty = CanonicalJson.SerializePretty(doc);

            Assert.Contains("\"a\": 2", pretty);
            Assert.Contains("\"b\": 1", pretty);
            Assert.True(pretty.IndexOf("\"a\"", StringComparison.Ordinal) < pretty.IndexOf("\"b\"", StringComparison.Ordinal));
        }

        // —— RFC 8785 向量子集（附录 B/D 抽样；完整附录向量随 AigcTotal.Log 落地一并补齐）——

        [Fact]
        public void Rfc8785_AppendixD_StringEscapes_Subset()
        {
            // RFC 8785 附录 D 代表字符：控制字符、DEL（0x7F 不转义）、引号、反斜杠、正斜杠、BMP 多字节
            var doc = new Dictionary<string, object?>
            {
                ["s"] = "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"/\u007f\u4e2d",
            };

            // 期望（RFC 8785 逐字转义规则）：€ 原样、\u000f 小写、\n 短转义、引号反斜杠转义、
            // '/' 不转义、DEL(0x7f) 原样、汉字原样
            Assert.Equal("{\"s\":\"\u20ac$\\u000f\\nA'B\\\"\\\\\\\\\\\"/\u007f\u4e2d\"}",
                CanonicalJson.Serialize(doc));
        }

        [Fact]
        public void Rfc8785_AppendixB_LoneSurrogates_Escaped()
        {
            // 孤立代理项：JCS 要求按 ES6 JSON.stringify 规则转义为 \udXXX（小写 hex）
            var high = new Dictionary<string, object?> { ["s"] = "\uD800" };
            var low = new Dictionary<string, object?> { ["s"] = "\uDC00" };

            Assert.Equal("{\"s\":\"\\ud800\"}", CanonicalJson.Serialize(high));
            Assert.Equal("{\"s\":\"\\udc00\"}", CanonicalJson.Serialize(low));

            // UTF-8 编码字节形态必须与 JCS 规范一致（可跨实现复算哈希）
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(CanonicalJson.Serialize(high));
            Assert.Equal("{\"s\":\"\\ud800\"}", System.Text.Encoding.UTF8.GetString(bytes));
        }

        [Fact]
        public void Rfc8785_ValidSurrogatePair_PassesThrough()
        {
            // 合法代理对（U+1D11E 音乐符号）：原样透传，编码为 4 字节 UTF-8
            var doc = new Dictionary<string, object?> { ["s"] = "\uD834\uDD1E" };

            Assert.Equal("{\"s\":\"\uD834\uDD1E\"}", CanonicalJson.Serialize(doc));
        }

        [Fact]
        public void DuplicateKey_InputImpossible_LastWriteWinsIsEncodedOnce()
        {
            // 重复键在信封组装层不可表达（Dictionary 语义），此处验证排序器对既有键序无副作用
            var doc = new Dictionary<string, object?> { ["a"] = 1L };
            doc["a"] = 2L;
            Assert.Equal("{\"a\":2}", CanonicalJson.Serialize(doc));
        }
    }

    public class UlidTests
    {
        [Fact]
        public void Format_26CharsCrockfordAlphabet()
        {
            string id = Ulid.NewUlid();
            Assert.Equal(26, id.Length);
            Assert.True(Ulid.IsValid(id));
        }

        [Fact]
        public void ExcludedLetters_Absent()
        {
            for (int i = 0; i < 100; i++)
            {
                string id = Ulid.NewUlid();
                Assert.DoesNotContain('I', id);
                Assert.DoesNotContain('L', id);
                Assert.DoesNotContain('O', id);
                Assert.DoesNotContain('U', id);
            }
        }

        [Fact]
        public void Unique_AcrossMany()
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < 10_000; i++)
            {
                Assert.True(seen.Add(Ulid.NewUlid()));
            }
        }

        [Fact]
        public void SameTimestamp_DistinctIds()
        {
            var ts = DateTimeOffset.UtcNow;
            var a = Ulid.NewUlid(ts);
            var b = Ulid.NewUlid(ts);
            Assert.NotEqual(a, b);
            // 前 9 字符纯时戳（48bit/5=9.6）；第 10 字符混入 2 位随机比特，不保证相同
            Assert.Equal(a[..9], b[..9]);
        }
    }
}
