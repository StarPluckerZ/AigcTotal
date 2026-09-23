using System;
using System.Security.Cryptography;

namespace AigcTotal.Log.Signing
{
#if NET
    /// <summary>
    /// BCL ECDsa 签名器（net10.0）：本地测试与开发期 KMS 替身，亦是 KMS 实现的行为参照——
    /// 输出 64 字节 P1363。生产签发一律走 KMS 实现，不用此类。
    /// </summary>
    public sealed class BclP256Signer : IReportSigner
    {
        private readonly ECDsa _key;

        public BclP256Signer(ECDsa key)
        {
            _key = key ?? throw new ArgumentNullException(nameof(key));
            if (_key.KeySize != 256) throw new ArgumentException("key must be P-256", nameof(key));
        }

        public byte[] Sign(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            byte[] signature = _key.SignData(data, HashAlgorithmName.SHA256);
            if (signature.Length != 64) throw new InvalidOperationException("unexpected signature length");
            return signature;
        }
    }
#endif
}
