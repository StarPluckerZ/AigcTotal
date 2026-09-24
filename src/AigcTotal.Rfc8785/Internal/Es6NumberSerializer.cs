using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace AigcTotal.Rfc8785.Internal
{
    /// <summary>
    /// ECMAScript Number::toString（ECMA-262 §7.1.12.1，含 Note 2「就近舍入、平局取偶」）
    /// 的精确实现，即 RFC 8785 §3.2.2.3 规定的 JSON 数字序列化算法。
    /// 以 BigInteger 精确算术求「最短往返十进制」（Dragon4 风格舍入区间法：
    /// 对 k = 1..17 依次取 v 最近邻的 k 位十进制，判其是否落入 v 的舍入区间；
    /// 区间端点在尾数为偶时闭、奇时开；2 的幂边界下方间隙减半），
    /// 不依赖运行时 double 格式化行为，netstandard2.0 与 net10.0 输出逐字节一致。
    /// </summary>
    internal static class Es6NumberSerializer
    {
        private static readonly BigInteger[] Pow10Table = CreatePow10Table();

        public static string Serialize(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new JsonCanonicalizationException(
                    "number is NaN or Infinity, which is not permitted in JSON (RFC 8785 §3.2.2.3)");
            }

            if (value == 0d)
            {
                return "0"; // 负零与正零同形（RFC 8785 附录 B：Minus zero → 0）
            }

            bool negative = value < 0d;
            long bits = BitConverter.DoubleToInt64Bits(Math.Abs(value));
            int biasedExponent = (int)((bits >> 52) & 0x7FF);
            long significandBits = bits & 0xFFFFFFFFFFFFFL;
            int e2;
            if (biasedExponent == 0)
            {
                e2 = -1074; // 次正规数：无隐含位，值 = m × 2^-1074
            }
            else
            {
                significandBits |= 1L << 52;
                e2 = biasedExponent - 1075; // 值 = m × 2^e2，m 为 53 位含隐含位尾数
            }

            // 2 的幂处上方间隙为 ULP、下方间隙为 ULP/2（前一 binade 的 ULP 减半）；
            // 最小正规数与次正规数的下方间隙仍为 ULP。
            bool lowerGapHalved = significandBits == (1L << 52) && biasedExponent > 1;
            BigInteger m = significandBits;

            int nEstimate = EstimateDecimalExponent(Math.Abs(value));
            for (int k = 1; k <= 17; k++)
            {
                (BigInteger s, int n) = LocateDigits(m, e2, k, nEstimate, lowerGapHalved);
                if (s.Sign > 0)
                {
                    return Render(s, n, negative);
                }
            }

            throw new InvalidOperationException("unreachable: every finite double has a 17-digit round-trip form");
        }

        /// <summary>
        /// 对给定位数 k 定位有效数字：在指数估计 ±1 范围内（10 的幂附近 floor(log10) 估计会偏 1）、
        /// 对每个指数取 floor 与 ceil 两个候选（2 的幂边界处舍入区间不对称，最近候选可能在
        /// 区间外而次近候选在区间内），保留所有「数位范围合法 × 落入舍入区间」的组合，
        /// 按 |s × 10^(n−k) − v| 最小者择优（ECMA-262 Note 2：平局取偶）。
        /// </summary>
        private static (BigInteger s, int n) LocateDigits(BigInteger m, int e2, int k, int nEstimate, bool lowerGapHalved)
        {
            BigInteger bestS = BigInteger.Zero;
            int bestN = nEstimate;
            (BigInteger num, BigInteger den)? bestDistance = null;
            for (int nTry = nEstimate - 1; nTry <= nEstimate + 1; nTry++)
            {
                BigInteger floorScaled = FloorOfScaled(m, e2, k - nTry);
                for (int offset = 0; offset < 2; offset++)
                {
                    BigInteger s = floorScaled + offset;
                    if (s < Pow10(k - 1) || s >= Pow10(k) || !IsInRoundingInterval(s, nTry - k, m, e2, lowerGapHalved))
                    {
                        continue;
                    }

                    (BigInteger num, BigInteger den) distance = DistanceToValue(s, nTry - k, m, e2);
                    int ordering = bestDistance is null
                        ? -1
                        : CompareFraction(distance.num, distance.den, bestDistance.Value.num, bestDistance.Value.den);
                    if (ordering < 0 || (ordering == 0 && s.IsEven))
                    {
                        bestDistance = distance;
                        bestS = s;
                        bestN = nTry;
                    }
                }
            }

            return (bestS, bestN);
        }

        /// <summary>精确计算 floor(v × 10^q)，v = m × 2^e2 &gt; 0。</summary>
        private static BigInteger FloorOfScaled(BigInteger m, int e2, int q)
        {
            BigInteger numerator = m;
            BigInteger denominator = BigInteger.One;
            if (q >= 0)
            {
                numerator *= BigInteger.Pow(5, q);
            }
            else
            {
                denominator *= BigInteger.Pow(5, -q);
            }

            int shift = e2 + q;
            if (shift >= 0)
            {
                numerator <<= shift;
            }
            else
            {
                denominator <<= -shift;
            }

            return numerator / denominator; // 两侧恒正，截断即 floor
        }

        /// <summary>|s × 10^p − m × 2^e2|，以（正分母）分数返回。</summary>
        private static (BigInteger num, BigInteger den) DistanceToValue(BigInteger s, int p, BigInteger m, int e2)
        {
            BigInteger candidateNum, candidateDen;
            if (p >= 0)
            {
                candidateNum = (s * BigInteger.Pow(5, p)) << p;
                candidateDen = BigInteger.One;
            }
            else
            {
                candidateNum = s;
                candidateDen = BigInteger.Pow(5, -p) << -p;
            }

            BigInteger valueNum, valueDen;
            if (e2 >= 0)
            {
                valueNum = m << e2;
                valueDen = BigInteger.One;
            }
            else
            {
                valueNum = m;
                valueDen = BigInteger.One << -e2;
            }

            return (BigInteger.Abs((candidateNum * valueDen) - (valueNum * candidateDen)), candidateDen * valueDen);
        }

        private static int CompareFraction(BigInteger aNum, BigInteger aDen, BigInteger bNum, BigInteger bDen)
        {
            return BigInteger.Compare(aNum * bDen, bNum * aDen);
        }

        /// <summary>
        /// 判断候选十进制 s × 10^(n−k) 是否落入 v = m × 2^e2 的舍入区间。
        /// 下界 L = v − g⁻/2，上界 U = v + g⁺/2；g⁺ = 2^e2，2 的幂边界（除最小正规数）g⁻ = 2^(e2−1)；
        /// 端点归属：尾数偶 → 闭区间，尾数奇 → 开区间（round-to-nearest-even）。
        /// </summary>
        private static bool IsInRoundingInterval(BigInteger s, int p, BigInteger m, int e2, bool lowerGapHalved)
        {
            BigInteger candidateNumerator;
            BigInteger candidateDenominator;
            if (p >= 0)
            {
                candidateNumerator = (s * BigInteger.Pow(5, p)) << p;
                candidateDenominator = BigInteger.One;
            }
            else
            {
                candidateNumerator = s;
                candidateDenominator = BigInteger.Pow(5, -p) << -p;
            }

            BigInteger lowNumerator = lowerGapHalved ? (4 * m) - 1 : (2 * m) - 1;
            int lowShift = lowerGapHalved ? e2 - 2 : e2 - 1;
            BigInteger highNumerator = (2 * m) + 1;
            int highShift = e2 - 1;
            bool even = m.IsEven;

            return (CompareCandidate(candidateNumerator, candidateDenominator, lowNumerator, lowShift) > 0
                    || (CompareCandidate(candidateNumerator, candidateDenominator, lowNumerator, lowShift) == 0 && even))
                && (CompareCandidate(candidateNumerator, candidateDenominator, highNumerator, highShift) < 0
                    || (CompareCandidate(candidateNumerator, candidateDenominator, highNumerator, highShift) == 0 && even));
        }

        /// <summary>比较 cNum/cDen 与 bNum × 2^shift（全部分母为正）。</summary>
        private static int CompareCandidate(BigInteger cNum, BigInteger cDen, BigInteger bNum, int shift)
        {
            if (shift >= 0)
            {
                return BigInteger.Compare(cNum, (bNum << shift) * cDen);
            }

            return BigInteger.Compare(cNum << -shift, bNum * cDen);
        }

        /// <summary>ECMA-262 §7.1.12.1 第 5 步：按有效数字 s（k 位）与十进制指数 n 渲染字符串。</summary>
        private static string Render(BigInteger s, int n, bool negative)
        {
            string digits = s.ToString(CultureInfo.InvariantCulture);
            int k = digits.Length;
            var sb = new StringBuilder(k + 8);
            if (negative)
            {
                sb.Append('-');
            }

            if (k <= n && n <= 21)
            {
                sb.Append(digits).Append('0', n - k);
            }
            else if (n > 0 && n <= 21)
            {
                sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
            }
            else if (-6 < n && n <= 0)
            {
                sb.Append("0.").Append('0', -n).Append(digits);
            }
            else
            {
                int exponent = n - 1;
                sb.Append(digits[0]);
                if (k > 1)
                {
                    sb.Append('.').Append(digits, 1, k - 1);
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

        private static int EstimateDecimalExponent(double positiveValue)
        {
            // 仅作初值；LocateDigits 用精确比较在 ±1 内校正
            return (int)Math.Floor(Math.Log10(positiveValue)) + 1;
        }

        private static BigInteger Pow10(int exponent)
        {
            if (exponent < 0 || exponent > 17)
            {
                throw new ArgumentOutOfRangeException(nameof(exponent));
            }

            return Pow10Table[exponent];
        }

        private static BigInteger[] CreatePow10Table()
        {
            var table = new BigInteger[18];
            BigInteger value = BigInteger.One;
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = value;
                value *= 10;
            }

            return table;
        }
    }
}
