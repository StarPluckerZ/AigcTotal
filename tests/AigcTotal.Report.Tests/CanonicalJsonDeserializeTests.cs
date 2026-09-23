using System;
using System.Collections.Generic;
using Xunit;

namespace AigcTotal.Report.Tests
{
    /// <summary>canonical JSON 读端：只接受 Serialize 产出的子集；防御与写端对称（浮点/重复键/尾随垃圾拒绝）。</summary>
    public class CanonicalJsonDeserializeTests
    {
        [Fact]
        public void Roundtrip_WithSerialize()
        {
            var doc = new Dictionary<string, object?>
            {
                ["verdict"] = "compliant",
                ["count"] = 42L,
                ["ok"] = true,
                ["nothing"] = null,
                ["list"] = new List<object?> { 1L, "x", new Dictionary<string, object?> { ["inner"] = false } },
            };

            var parsed = CanonicalJson.Deserialize(CanonicalJson.Serialize(doc));

            Assert.Equal("compliant", parsed["verdict"]);
            Assert.Equal(42L, parsed["count"]);
            Assert.Equal(true, parsed["ok"]);
            Assert.Null(parsed["nothing"]);
            var list = Assert.IsType<List<object?>>(parsed["list"]);
            Assert.Equal(3, list.Count);
            var inner = Assert.IsType<Dictionary<string, object?>>(list[2]);
            Assert.Equal(false, inner["inner"]);
        }

        [Fact]
        public void Rejects_Floats_And_DuplicateKeys_And_TrailingContent()
        {
            Assert.Throws<FormatException>(() => CanonicalJson.Deserialize("{\"a\":1.5}"));
            Assert.Throws<FormatException>(() => CanonicalJson.Deserialize("{\"a\":1,\"a\":2}"));
            Assert.Throws<FormatException>(() => CanonicalJson.Deserialize("{\"a\":1} x"));
            Assert.Throws<FormatException>(() => CanonicalJson.Deserialize("{\"a\":}"));
            Assert.Throws<FormatException>(() => CanonicalJson.Deserialize("[1,2]")); // 顶层必须为对象
        }

        [Fact]
        public void Accepts_Escapes_And_LoneSurrogates()
        {
            var parsed = CanonicalJson.Deserialize("{\"s\":\"a\\nb\\u0001\\ud800\"}");
            string s = Assert.IsType<string>(parsed["s"]);
            Assert.Equal("a\nb\u0001\ud800", s);
        }
    }
}
