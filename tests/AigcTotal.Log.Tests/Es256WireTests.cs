using System;
using System.Security.Cryptography;
using AigcTotal.Log.Signing;
using Xunit;

namespace AigcTotal.Log.Tests
{
    /// <summary>ES256 线格式：SPKI 模板（RFC 5480 定长前缀）、kid 派生、DER↔P1363 签名互转、BCL 交叉验证。</summary>
    public class Es256WireTests
    {
        // P-256 生成元 G（著名常量）：压缩标志 0x04 + X + Y = 65 字节非压缩点
        private const string Gx = "6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296";
        private const string Gy = "4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5";

        // RFC 5480 P-256 SPKI 前缀：SEQ{ SEQ{ OID ecPublicKey, OID P-256 }, BIT STRING(0x00||point) }
        private const string SpkiPrefix = "3059301306072a8648ce3d020106082a8648ce3d030107034200";

        private static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        [Fact]
        public void BuildSpki_KnownAnswer_WithGeneratorPoint()
        {
            byte[] point = HexToBytes("04" + Gx + Gy);
            byte[] spki = Es256Wire.BuildSpkiFromPoint(point);

            Assert.Equal(91, spki.Length);
            Assert.Equal(SpkiPrefix + "04" + Gx + Gy, Hex(spki));
        }

        [Fact]
        public void BuildSpki_RejectsWrongPointForm()
        {
            Assert.Throws<ArgumentException>(() => Es256Wire.BuildSpkiFromPoint(new byte[64])); // 缺 0x04 前缀
            Assert.Throws<ArgumentException>(() => Es256Wire.BuildSpkiFromPoint(new byte[33]));
        }

        [Fact]
        public void Kid_DerivedFromSpkiSha256()
        {
            byte[] spki = HexToBytes(SpkiPrefix + "04" + Gx + Gy);
            byte[] digest = SHA256.HashData(spki);
            var sb = new System.Text.StringBuilder("k");
            foreach (byte b in digest) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));

            Assert.Equal(sb.ToString(), Es256Wire.KidFromSpki(spki));
            Assert.Equal(65, Es256Wire.KidFromSpki(spki).Length);
        }

        [Fact]
        public void Der_Roundtrip_And_HandBuiltVector()
        {
            // 手工向量：r=1, s=0xFF → SEQ{ INT 01, INT FF }（无前导零填充情形）
            var p1363 = new byte[64];
            p1363[31] = 0x01;      // r = 1
            p1363[63] = 0xFF;      // s = 0xFF
            byte[] der = Es256Wire.P1363ToDer(p1363);
            Assert.Equal("3007020101020200ff", Hex(der)); // s=255 需 0x00 前缀防负数
            Assert.Equal(p1363, Es256Wire.DerToP1363(der));

            // 高位字节需 0x00 填充：r = 2^255（首字节 0x80）→ INT 前导 00；s = 0 → INT 最小编码 01 00
            var high = new byte[64];
            high[0] = 0x80;
            byte[] der2 = Es256Wire.P1363ToDer(high);
            string expected = "3026" + "022100" + "80" + new string('0', 62) + "020100";
            Assert.Equal(expected, Hex(der2));
            Assert.Equal(high, Es256Wire.DerToP1363(der2));
        }

        [Fact]
        public void DerToP1363_RejectsGarbage()
        {
            Assert.Throws<FormatException>(() => Es256Wire.DerToP1363(new byte[] { 0x30, 0x05 }));
            Assert.Throws<FormatException>(() => Es256Wire.DerToP1363(new byte[] { 0x02, 0x01, 0x01 }));
        }

        [Fact]
        public void EndToEnd_BclSign_DerWire_Verify()
        {
            // 端到端：BCL 签名（P1363）→ DER → 反解 → BCL 验证。交叉验证 DER 编码正确性。
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] data = new byte[] { 1, 2, 3, 4, 5 };
            byte[] p1363 = key.SignData(data, HashAlgorithmName.SHA256);

            byte[] der = Es256Wire.P1363ToDer(p1363);
            byte[] back = Es256Wire.DerToP1363(der);

            Assert.Equal(p1363, back);
            Assert.True(key.VerifyData(data, back, HashAlgorithmName.SHA256));
        }
    }
}
