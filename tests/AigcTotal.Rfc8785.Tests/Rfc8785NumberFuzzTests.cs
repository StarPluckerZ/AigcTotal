using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace AigcTotal.Rfc8785.Tests
{
    /// <summary>
    /// RFC 8785 附录 B 建议的「more exhaustive validation」替代做法：
    /// 以「G{p} 逐位扫描 + parse 往返判定」为最短数字预言机做大规模差分，并做逐位往返校验。
    /// 注意：不用 ToString("R") 作预言机——实测 .NET 10 的 R 在 2 的幂边界
    /// （如 bits 0410000000000000）会给出不满足往返的 16 位输出（其自身 parse 即回到下邻），
    /// 而 V8 与逐位精确舍入区间法均给出 17 位；G{p} + 往返判定与 V8 完全一致。
    /// </summary>
    public class Rfc8785NumberFuzzTests
    {
        private const int FullRandomSamples = 120_000;
        private const int NormalSamples = 40_000;
        private const int SubnormalSamples = 20_000;

        private readonly ITestOutputHelper _output;

        public Rfc8785NumberFuzzTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Serialize_matches_ryu_shortest_digits_on_fuzz_corpus()
        {
            ulong state = 0x243F6A8885A308D3UL;
            int checkedSamples = 0;

            checkedSamples += CheckMany(() => NextRandom(ref state), FullRandomSamples, "full-random");
            checkedSamples += CheckMany(() => NextNormal(ref state), NormalSamples, "normal-binades");
            checkedSamples += CheckMany(() => NextSubnormal(ref state), SubnormalSamples, "subnormals");
            foreach (double sample in StructuredSamples)
            {
                CheckOne(sample, "structured");
                checkedSamples++;
            }

            _output.WriteLine($"differential samples checked: {checkedSamples}");
            Assert.Equal(FullRandomSamples + NormalSamples + SubnormalSamples + StructuredSamples.Length, checkedSamples);
        }

        private static int CheckMany(Func<double> source, int count, string label)
        {
            for (int i = 0; i < count; i++)
            {
                CheckOne(source(), label);
            }

            return count;
        }

        private static void CheckOne(double value, string label)
        {
            string expected = ShortestEs6Oracle(value);
            string actual = JsonCanonicalizer.Serialize(value);
            if (actual != expected)
            {
                Assert.Fail(
                    $"{label} mismatch for bits {Rfc8785AppendixB.DoubleToBits(value)}: " +
                    $"expected '{expected}' (oracle) but got '{actual}'");
            }

            VerifyRoundTrip(value, actual, label);
        }

        private static void VerifyRoundTrip(double value, string canonical, string label)
        {
            if (value == 0d)
            {
                return; // "0" 解析回 +0，与 -0 的位形差异是 RFC 附录 B 规定行为
            }

            double parsed = double.Parse(canonical, CultureInfo.InvariantCulture);
            if (BitConverter.DoubleToInt64Bits(parsed) != BitConverter.DoubleToInt64Bits(value))
            {
                Assert.Fail(
                    $"{label} round-trip failure for bits {Rfc8785AppendixB.DoubleToBits(value)}: " +
                    $"'{canonical}' parses to {Rfc8785AppendixB.DoubleToBits(parsed)}");
            }
        }

        // ---- 采样器（确定性 xorshift64*）----

        private static ulong NextRandom(ref ulong state)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            ulong r = state * 2685821657736338717UL;
            if (((r >> 52) & 0x7FF) == 0x7FF) // 指数域全 1（NaN/Inf，含负号）换轨为有限值
            {
                r ^= 1UL << 52;
            }

            return r;
        }

        private static double NextNormal(ref ulong state)
        {
            ulong r = NextRandom(ref state);
            ulong biased = 1 + (r % 2046); // 1..2046，覆盖全部正规 binade
            return BitConverter.Int64BitsToDouble(unchecked((long)((r & 0x000FFFFFFFFFFFFFUL) | (biased << 52))));
        }

        private static double NextSubnormal(ref ulong state)
        {
            ulong r = NextRandom(ref state) & 0x000FFFFFFFFFFFFFUL;
            if (r == 0)
            {
                r = 1;
            }

            return BitConverter.Int64BitsToDouble(unchecked((long)r));
        }

        private static readonly double[] StructuredSamples = BuildStructuredSamples();

        private static double[] BuildStructuredSamples()
        {
            var samples = new System.Collections.Generic.List<double>
            {
                0d,
                -0d,
                double.Epsilon,
                -double.Epsilon,
                double.MaxValue,
                -double.MaxValue,
                double.MinValue,
                1d,
                -1d,
            };

            for (int exp = -324; exp <= 308; exp++)
            {
                double powerOfTen = Math.Pow(10, exp);
                foreach (double v in new[] { powerOfTen, BitwiseNeighbor(powerOfTen, -1), BitwiseNeighbor(powerOfTen, +1) })
                {
                    if (!double.IsNaN(v))
                    {
                        samples.Add(v);
                        samples.Add(-v);
                    }
                }
            }

            for (int exp = -1074; exp <= 1023; exp++)
            {
                double powerOfTwo = Math.Pow(2, exp);
                if (double.IsInfinity(powerOfTwo))
                {
                    break;
                }

                samples.Add(powerOfTwo);
                samples.Add(BitwiseNeighbor(powerOfTwo, -1));
                samples.Add(BitwiseNeighbor(powerOfTwo, +1));
                samples.Add(-powerOfTwo);
            }

            for (long v = -1000; v <= 1000; v++)
            {
                samples.Add(v);
            }

            for (long v = -20; v <= 20; v++)
            {
                samples.Add(v + 0.5d);
                samples.Add(v + 0.25d);
                samples.Add(v + 0.125d);
            }

            return samples.ToArray();
        }

        private static double BitwiseNeighbor(double value, int direction)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            bits += direction;
            return BitConverter.Int64BitsToDouble(bits);
        }

        // ---- 预言机：E{p} 候选扫描 + 精确距离择优 → 独立 ES6 渲染 ----
        //
        // ES6 最短表示 = 最小位数 k 下「可往返」的 k 位十进制中离 v 最近者（平局取偶）。
        // 注意「正确舍入的 p 位十进制」（G{p}）未必可往返：2 的幂边界舍入区间不对称，
        // 可往返的可能是 ±1 位变体（V8 对 0x0060000000000000 输出 …045 而 G16 = …044 不可往返）。
        // 因此对每个 p 以 E{p-1} 定位基准，扫描 {−1, 0, +1} 三个候选，逐一验证往返、按精确距离择优。

        public static string ShortestEs6Oracle(double value)
        {
            if (value == 0d)
            {
                return "0";
            }

            double magnitude = Math.Abs(value);
            long targetBits = BitConverter.DoubleToInt64Bits(magnitude);
            (BigInteger vNum, BigInteger vDen) = ValueAsFraction(magnitude);
            for (int p = 1; p <= 17; p++)
            {
                (BigInteger baseDigits, int n) = ScientificDigits(magnitude, p);
                (BigInteger? digits, (BigInteger num, BigInteger den)? dist) best = (null, null);
                foreach (int delta in new[] { 0, 1, -1 })
                {
                    BigInteger candidate = baseDigits + delta;
                    if (candidate < Pow10BigInt(p - 1) || candidate >= Pow10BigInt(p))
                    {
                        continue;
                    }

                    string test = candidate.ToString(CultureInfo.InvariantCulture) + "E" + (n - p).ToString(CultureInfo.InvariantCulture);
                    double parsed = double.Parse(test, CultureInfo.InvariantCulture);
                    if (BitConverter.DoubleToInt64Bits(parsed) != targetBits)
                    {
                        continue;
                    }

                    (BigInteger num, BigInteger den) distance = DistanceTo(candidate, n, vNum, vDen);
                    int ordering = best.dist is null
                        ? -1
                        : BigInteger.Compare(distance.num * best.dist.Value.den, best.dist.Value.num * distance.den);
                    if (ordering < 0 || (ordering == 0 && candidate.IsEven))
                    {
                        best = (candidate, distance);
                    }
                }

                if (best.digits is not null)
                {
                    return RenderEs6(best.digits.Value.ToString(CultureInfo.InvariantCulture), n, value < 0);
                }
            }

            throw new InvalidOperationException(
                "oracle exhausted for bits " + BitConverter.DoubleToInt64Bits(magnitude).ToString("X16", CultureInfo.InvariantCulture));
        }

        /// <summary>v 的 E{p-1} 科学计数：返回有效数字 D 与十进制指数 n（v = 0.D × 10^n）。</summary>
        private static (BigInteger digits, int n) ScientificDigits(double magnitude, int p)
        {
            string e = magnitude.ToString("E" + (p - 1), CultureInfo.InvariantCulture);
            int eIndex = e.IndexOf('E', StringComparison.Ordinal);
            string mantissa = e[..eIndex].Replace(".", string.Empty, StringComparison.Ordinal);
            int exponent = int.Parse(e[(eIndex + 1)..], CultureInfo.InvariantCulture);
            return (BigInteger.Parse(mantissa, CultureInfo.InvariantCulture), exponent + 1);
        }

        private static BigInteger Pow10BigInt(int exponent)
        {
            BigInteger value = BigInteger.One;
            for (int i = 0; i < exponent; i++)
            {
                value *= 10;
            }

            return value;
        }

        /// <summary>v = m × 2^e2 的精确分数。</summary>
        private static (BigInteger num, BigInteger den) ValueAsFraction(double magnitude)
        {
            long bits = BitConverter.DoubleToInt64Bits(magnitude);
            int biased = (int)((bits >> 52) & 0x7FF);
            BigInteger m = bits & 0xFFFFFFFFFFFFFL;
            if (biased == 0)
            {
                return (m, BigInteger.One << 1074);
            }

            m |= BigInteger.One << 52;
            int e2 = biased - 1075;
            return e2 >= 0 ? (m << e2, BigInteger.One) : (m, BigInteger.One << -e2);
        }

        /// <summary>|D × 10^(n−k) − v| 的精确分数，k 为 D 的位数；用于同位数候选的就近比较。</summary>
        private static (BigInteger num, BigInteger den) DistanceTo(BigInteger digits, int n, BigInteger vNum, BigInteger vDen)
        {
            int k = digits.ToString(CultureInfo.InvariantCulture).Length;
            int p = n - k;
            BigInteger cNum, cDen;
            if (p >= 0)
            {
                cNum = digits * Pow10BigInt(p);
                cDen = BigInteger.One;
            }
            else
            {
                cNum = digits;
                cDen = Pow10BigInt(-p);
            }

            return (BigInteger.Abs((cNum * vDen) - (vNum * cDen)), cDen * vDen);
        }

        /// <summary>测试内独立实现的 ECMA-262 §7.1.12.1 渲染（与库内部实现互为独立写法）。</summary>
        private static string RenderEs6(string digits, int n, bool negative)
        {
            int k = digits.Length;
            var sb = new StringBuilder();
            if (negative)
            {
                sb.Append('-');
            }

            if (k <= n && n <= 21)
            {
                sb.Append(digits);
                sb.Append('0', n - k);
            }
            else if (n > 0 && n <= 21)
            {
                sb.Append(digits.AsSpan(0, n)).Append('.').Append(digits.AsSpan(n));
            }
            else if (n > -6 && n <= 0)
            {
                sb.Append("0.");
                sb.Append('0', -n);
                sb.Append(digits);
            }
            else
            {
                int exponent = n - 1;
                sb.Append(digits[0]);
                if (k > 1)
                {
                    sb.Append('.').Append(digits.AsSpan(1));
                }

                sb.Append('e');
                if (exponent >= 0)
                {
                    sb.Append('+');
                }
                else
                {
                    sb.Append('-');
                }

                sb.Append(Math.Abs(exponent).ToString(CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }
}
