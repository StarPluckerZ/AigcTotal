using System;
using System.Security.Cryptography;

namespace AigcTotal.Log.Signing
{
    /// <summary>
    /// ES256 验签（ECDSA P-256 + SHA-256，签名 = 64 字节 P1363）：
    /// net10 用 BCL ECDsa.VerifyData；netstandard2.0 引 BouncyCastle（仅验证侧，见技术决策 6.7）。
    /// 输入为 SPKI DER——与 kid 派生、keys.json 的 JWK 构建共用同一素材。
    /// </summary>
    public static class Es256Verifier
    {
        public static bool Verify(byte[] message, byte[] p1363Signature, byte[] spkiDer)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (p1363Signature == null) throw new ArgumentNullException(nameof(p1363Signature));
            if (spkiDer == null) throw new ArgumentNullException(nameof(spkiDer));
            if (p1363Signature.Length != 64)
            {
                throw new ArgumentException("P1363 signature must be 64 bytes", nameof(p1363Signature));
            }
#if NET
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spkiDer, out _);
            return key.VerifyData(message, p1363Signature, HashAlgorithmName.SHA256);
#else
            return VerifyWithBouncyCastle(message, p1363Signature, spkiDer);
#endif
        }

#if !NET
        private static bool VerifyWithBouncyCastle(byte[] message, byte[] p1363Signature, byte[] spkiDer)
        {
            var publicKey = (Org.BouncyCastle.Crypto.Parameters.ECPublicKeyParameters)
                Org.BouncyCastle.Security.PublicKeyFactory.CreateKey(spkiDer);
            byte[] digest;
            using (var sha = SHA256.Create())
            {
                digest = sha.ComputeHash(message);
            }
            var r = new Org.BouncyCastle.Math.BigInteger(1, p1363Signature, 0, 32);
            var s = new Org.BouncyCastle.Math.BigInteger(1, p1363Signature, 32, 32);
            var signer = new Org.BouncyCastle.Crypto.Signers.ECDsaSigner();
            signer.Init(false, publicKey);
            return signer.VerifySignature(digest, r, s);
        }
#endif
    }
}
