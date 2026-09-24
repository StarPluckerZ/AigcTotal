using System;
using System.Collections.Generic;

namespace AigcTotal.Rfc8785.Tests
{
    /// <summary>
    /// RFC 8785 附录 B「Number Serialization Samples」表 1 逐字向量：
    /// IEEE 754 十六进制 → 期望 JSON 形态；期望为 null 表示必须报错（NaN/Infinity，注 (3)）。
    /// </summary>
    public static class Rfc8785AppendixB
    {
        public static readonly IReadOnlyList<(string Ieee754Hex, string? ExpectedJson, string Comment)> Rows =
            new (string, string?, string)[]
            {
                ("0000000000000000", "0", "Zero"),
                ("8000000000000000", "0", "Minus zero"),
                ("0000000000000001", "5e-324", "Min pos number"),
                ("8000000000000001", "-5e-324", "Min neg number"),
                ("7fefffffffffffff", "1.7976931348623157e+308", "Max pos number"),
                ("ffefffffffffffff", "-1.7976931348623157e+308", "Max neg number"),
                ("4340000000000000", "9007199254740992", "Max pos int (1)"),
                ("c340000000000000", "-9007199254740992", "Max neg int (1)"),
                ("4430000000000000", "295147905179352830000", "~2**68 (2)"),
                ("7fffffffffffffff", null, "NaN (3)"),
                ("7ff0000000000000", null, "Infinity (3)"),
                ("44b52d02c7e14af5", "9.999999999999997e+22", string.Empty),
                ("44b52d02c7e14af6", "1e+23", string.Empty),
                ("44b52d02c7e14af7", "1.0000000000000001e+23", string.Empty),
                ("444b1ae4d6e2ef4e", "999999999999999700000", string.Empty),
                ("444b1ae4d6e2ef4f", "999999999999999900000", string.Empty),
                ("444b1ae4d6e2ef50", "1e+21", string.Empty),
                ("3eb0c6f7a0b5ed8c", "9.999999999999997e-7", string.Empty),
                ("3eb0c6f7a0b5ed8d", "0.000001", string.Empty),
                ("41b3de4355555553", "333333333.3333332", string.Empty),
                ("41b3de4355555554", "333333333.33333325", string.Empty),
                ("41b3de4355555555", "333333333.3333333", string.Empty),
                ("41b3de4355555556", "333333333.3333334", string.Empty),
                ("41b3de4355555557", "333333333.33333343", string.Empty),
                ("becbf647612f3696", "-0.0000033333333333333333", string.Empty),
                ("43143ff3c1cb0959", "1424953923781206.2", "Round to even (4)"),
            };

        public static double BitsToDouble(string ieee754Hex)
        {
            return BitConverter.Int64BitsToDouble(unchecked((long)Convert.ToUInt64(ieee754Hex, 16)));
        }

        public static string DoubleToBits(double value)
        {
            return BitConverter.DoubleToInt64Bits(value).ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
