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
