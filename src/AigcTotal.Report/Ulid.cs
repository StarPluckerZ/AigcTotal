using System;
using System.Security.Cryptography;
using System.Text;

namespace AigcTotal.Report
{
    /// <summary>
    /// ULID（26 字符 Crockford base32 = 48bit 毫秒时戳 + 80bit CSPRNG 随机）。
    /// 时间有序：报告天然按签发顺序排列；同毫秒碰撞概率 2^-40 量级。
    /// </summary>
    public static class Ulid
    {
        // Crockford base32：排除 I L O U
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        public static string NewUlid()
        {
            return NewUlid(DateTimeOffset.UtcNow);
        }

        public static string NewUlid(DateTimeOffset timestamp)
        {
            long ms = timestamp.ToUnixTimeMilliseconds();
            if (ms < 0 || ms > 0xFFFFFFFFFFFFL)
            {
                throw new ArgumentOutOfRangeException(nameof(timestamp), "timestamp out of ULID 48-bit range");
            }

            var data = new byte[16];
            // 48-bit 毫秒时戳，大端，占据前 6 字节
            data[0] = (byte)(ms >> 40);
            data[1] = (byte)(ms >> 32);
            data[2] = (byte)(ms >> 24);
            data[3] = (byte)(ms >> 16);
            data[4] = (byte)(ms >> 8);
            data[5] = (byte)ms;
            // 80bit 随机，占据后 10 字节
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(data, 6, 10);
            }

            // 130-bit 视图：前置 2 个零位 + 128 数据位，切成 26 个 5-bit 组
            var sb = new StringBuilder(26);
            int bitPos = 2;
            for (int k = 0; k < 26; k++)
            {
                int v = 0;
                for (int b = 0; b < 5; b++)
                {
                    int idx = bitPos + b;
                    int bit = 0;
                    int dataBit = idx - 2;
                    if (dataBit >= 0 && dataBit < 128)
                    {
                        bit = (data[dataBit / 8] >> (7 - (dataBit % 8))) & 1;
                    }
                    v = (v << 1) | bit;
                }
                sb.Append(Alphabet[v]);
                bitPos += 5;
            }
            return sb.ToString();
        }

        /// <summary>校验字符串是否为合法 ULID 形态（26 字符、合法字母表）。</summary>
        public static bool IsValid(string value)
        {
            if (value == null || value.Length != 26) return false;
            foreach (char c in value)
            {
                if (Alphabet.IndexOf(c) < 0) return false;
            }
            return true;
        }
    }
}
