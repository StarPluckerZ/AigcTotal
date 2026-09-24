using System;
using Xunit;

namespace AigcTotal.Rfc8785.Tests
{
    /// <summary>RFC 8785 附录 B 表 1：全部 26 行向量（24 个值行 + NaN/Infinity 两个错误行）。</summary>
    public class Rfc8785AppendixBTests
    {
        [Fact]
        public void Serialize_every_table1_value_row_matches_the_rfc()
        {
            foreach (var row in Rfc8785AppendixB.Rows)
            {
                if (row.ExpectedJson is null)
                {
                    continue;
                }

                double value = Rfc8785AppendixB.BitsToDouble(row.Ieee754Hex);
                Assert.Equal(row.ExpectedJson, JsonCanonicalizer.Serialize(value));
            }
        }

        [Fact]
        public void Serialize_table1_nan_and_infinity_rows_terminate()
        {
            foreach (var row in Rfc8785AppendixB.Rows)
            {
                if (row.ExpectedJson is not null)
                {
                    continue;
                }

                double value = Rfc8785AppendixB.BitsToDouble(row.Ieee754Hex);
                var ex = Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Serialize(value));
                Assert.Contains("NaN or Infinity", ex.Message);
            }
        }

        [Fact]
        public void Serialize_negative_infinity_terminates_as_well()
        {
            Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Serialize(double.NegativeInfinity));
        }
    }
}
