using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace AigcTotal.Log.Signing
{
    /// <summary>
    /// ES256（ECDSA P-256 + SHA-256）线格式：RFC 5480 SPKI 模板（kid 派生与验证共用）、
    /// DER ↔ P1363 签名互转（KMS 输出多为 DER，checkpoint 传输形态为 P1363 64 字节的 base64url）、
    /// base64url。纯字节工作，双 TFM 通用；验证调用本身由宿主（CLI/server）的加密后端承担。
    /// </summary>
    public static class Es256Wire
    {
        // RFC 5480 P-256 SPKI 定长前缀：SEQ(89){ SEQ(15){ OID 1.2.840.10045.2.1, OID 1.2.840.10045.3.1.7 }, BIT STRING(66){ 00, point } }
        private static readonly byte[] SpkiPrefix =
        {
            0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01,
            0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00,
        };

        private const int PointLength = 65;   // 0x04 || X(32) || Y(32)
        private const int SpkiLength = 26 + PointLength;
        private const int ComponentLength = 32;

        public static byte[] BuildSpkiFromPoint(ReadOnlySpan<byte> uncompressedPoint)
        {
            if (uncompressedPoint.Length != PointLength || uncompressedPoint[0] != 0x04)
            {
                throw new ArgumentException("expected 65-byte uncompressed point (0x04 || X || Y)");
            }
            var spki = new byte[SpkiLength];
            SpkiPrefix.CopyTo(spki, 0);
            uncompressedPoint.CopyTo(spki.AsSpan(SpkiPrefix.Length));
            return spki;
        }

        /// <summary>由 keys.json 的 JWK（base64url 的 x/y）构建 SPKI。</summary>
        public static byte[] BuildSpkiFromJwk(IReadOnlyDictionary<string, string> jwk)
        {
            if (jwk == null) throw new ArgumentNullException(nameof(jwk));
            byte[] x = Base64UrlDecode(jwk["x"]);
            byte[] y = Base64UrlDecode(jwk["y"]);
            var point = new byte[PointLength];
            point[0] = 0x04;
            if (x.Length != ComponentLength || y.Length != ComponentLength)
            {
                throw new ArgumentException("jwk x/y must decode to 32 bytes");
            }
            x.CopyTo(point, 1);
            y.CopyTo(point, 1 + ComponentLength);
            return BuildSpkiFromPoint(point);
        }

        /// <summary>kid = "k" + SHA-256(SPKI DER) 的 64 位小写 hex（构造性无碰撞）。</summary>
        public static string KidFromSpki(ReadOnlySpan<byte> spki)
        {
            byte[] digest = Rfc6962Sha256(spki);
            var sb = new System.Text.StringBuilder(65);
            sb.Append('k');
            foreach (byte b in digest)
            {
                sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>64 字节 P1363（r‖s，各 32 字节定长）→ DER SEQUENCE{INTEGER,INTEGER}（最小编码 + 高位填充）。</summary>
        public static byte[] P1363ToDer(ReadOnlySpan<byte> p1363)
        {
            if (p1363.Length != 2 * ComponentLength)
            {
                throw new ArgumentException("P1363 signature must be 64 bytes");
            }
            byte[] r = TrimLeadingZeros(p1363.Slice(0, ComponentLength));
            byte[] s = TrimLeadingZeros(p1363.Slice(ComponentLength));
            int content = 2 + r.Length + 2 + s.Length;
            var der = new byte[2 + content];
            der[0] = 0x30;
            der[1] = (byte)content;
            der[2] = 0x02;
            der[3] = (byte)r.Length;
            r.CopyTo(der.AsSpan(4));
            der[4 + r.Length] = 0x02;
            der[5 + r.Length] = (byte)s.Length;
            s.CopyTo(der.AsSpan(6 + r.Length));
            return der;
        }

        /// <summary>DER ECDSA-Sig-Value → 64 字节 P1363。格式不符一律 FormatException。</summary>
        public static byte[] DerToP1363(ReadOnlySpan<byte> der)
        {
            int pos = 0;
            ReadByte(der, ref pos, 0x30);
            int seqLen = ReadLength(der, ref pos);
            if (seqLen != der.Length - pos) throw new FormatException("SEQUENCE length mismatch");

            byte[] r = ReadInteger(der, ref pos);
            byte[] s = ReadInteger(der, ref pos);
            if (pos != der.Length) throw new FormatException("trailing bytes after signature");

            var result = new byte[2 * ComponentLength];
            r.CopyTo(result, ComponentLength - r.Length);
            s.CopyTo(result, 2 * ComponentLength - s.Length);
            return result;
        }

        public static string Base64UrlEncode(ReadOnlySpan<byte> data)
        {
            return Convert.ToBase64String(data.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>
        /// 严格 base64url 解码（形态封闭）：仅接受 RFC 4648 §5 无填充字母表（A-Z a-z 0-9 - _），
        /// 长度 ≡ 1 (mod 4) 非法；标准 base64 字符（+ /）与 '=' 填充一律 FormatException——
        /// 同一签名只允许一种线形态（2026-09-24 §2-#9 收紧）。
        /// </summary>
        public static byte[] Base64UrlDecode(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (text.Length == 0) throw new FormatException("empty base64url input");
            if (text.Length % 4 == 1) throw new FormatException("invalid base64url length");

            var canonical = new char[text.Length + Padding(text.Length)];
            int o = 0;
            foreach (char c in text)
            {
                char mapped;
                if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9') mapped = c;
                else if (c == '-') mapped = '+';
                else if (c == '_') mapped = '/';
                else throw new FormatException($"invalid base64url character '{c}' (U+{(int)c:X4})");
                canonical[o++] = mapped;
            }
            while (o < canonical.Length) canonical[o++] = '=';

            try
            {
                return Convert.FromBase64CharArray(canonical, 0, canonical.Length);
            }
            catch (FormatException ex)
            {
                throw new FormatException("invalid base64url input: " + ex.Message, ex);
            }
        }

        private static int Padding(int length) => (4 - length % 4) % 4;

        /// <summary>大端整数规范化：仅剥前导零（尾零是低位有效字节，绝不可剥），高位为 1 时补 0x00 防负数。</summary>
        private static byte[] TrimLeadingZeros(ReadOnlySpan<byte> component)
        {
            int start = 0;
            while (start < component.Length - 1 && component[start] == 0) start++;
            int length = component.Length - start;
            bool pad = (component[start] & 0x80) != 0;
            var result = new byte[length + (pad ? 1 : 0)];
            if (pad) result[0] = 0x00;
            component.Slice(start, length).CopyTo(result.AsSpan(pad ? 1 : 0));
            return result;
        }

        private static byte[] ReadInteger(ReadOnlySpan<byte> der, ref int pos)
        {
            ReadByte(der, ref pos, 0x02);
            int length = ReadLength(der, ref pos);
            if (length < 1 || pos + length > der.Length) throw new FormatException("bad INTEGER length");
            if (length > 1 && der[pos] == 0)
            {
                pos++; // 前导 00（高位填充）；单字节 0x00 是合法值 0，不是填充
                length--;
            }
            if (length == 0 || length > ComponentLength) throw new FormatException("INTEGER length out of range");
            var value = new byte[length];
            der.Slice(pos, length).CopyTo(value);
            pos += length;
            return value;
        }

        private static void ReadByte(ReadOnlySpan<byte> der, ref int pos, byte expected)
        {
            if (pos >= der.Length || der[pos] != expected) throw new FormatException("unexpected DER tag");
            pos++;
        }

        private static int ReadLength(ReadOnlySpan<byte> der, ref int pos)
        {
            if (pos >= der.Length) throw new FormatException("unexpected end of DER");
            int length = der[pos++];
            if (length > 0x7F) throw new FormatException("long-form lengths not expected here");
            return length;
        }

        private static byte[] Rfc6962Sha256(ReadOnlySpan<byte> data)
        {
#if NET
            return SHA256.HashData(data);
#else
            using var sha = SHA256.Create();
            return sha.ComputeHash(data.ToArray());
#endif
        }
    }
}
