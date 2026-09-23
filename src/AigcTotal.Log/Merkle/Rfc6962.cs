using System;
using System.Security.Cryptography;

namespace AigcTotal.Log.Merkle
{
    /// <summary>
    /// RFC 6962 §2.1 域分离哈希：叶 = SHA-256(0x00 ‖ leaf)，节点 = SHA-256(0x01 ‖ left ‖ right)。
    /// 域分离防结构混淆攻击（叶串与内部节点串互不可伪造）；空树根 = SHA-256(空串)。
    /// </summary>
    public static class Rfc6962
    {
        public const byte LeafPrefix = 0x00;
        public const byte NodePrefix = 0x01;

        public static byte[] LeafHash(ReadOnlySpan<byte> leafData)
        {
#if NET
            var buffer = new byte[1 + leafData.Length];
            buffer[0] = LeafPrefix;
            leafData.CopyTo(buffer.AsSpan(1));
            return SHA256.HashData(buffer);
#else
            using var sha = SHA256.Create();
            sha.TransformBlock(new byte[] { LeafPrefix }, 0, 1, null, 0);
            var bytes = leafData.ToArray();
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return sha.Hash!;
#endif
        }

        public static byte[] NodeHash(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
#if NET
            var buffer = new byte[1 + left.Length + right.Length];
            buffer[0] = NodePrefix;
            left.CopyTo(buffer.AsSpan(1));
            right.CopyTo(buffer.AsSpan(1 + left.Length));
            return SHA256.HashData(buffer);
#else
            using var sha = SHA256.Create();
            sha.TransformBlock(new byte[] { NodePrefix }, 0, 1, null, 0);
            var l = left.ToArray();
            var r = right.ToArray();
            sha.TransformBlock(l, 0, l.Length, null, 0);
            sha.TransformBlock(r, 0, r.Length, null, 0);
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return sha.Hash!;
#endif
        }

        internal static byte[] EmptyHash()
        {
#if NET
            return SHA256.HashData(Array.Empty<byte>());
#else
            using var sha = SHA256.Create();
            return sha.ComputeHash(Array.Empty<byte>());
#endif
        }

        internal static byte[] Sha256(ReadOnlySpan<byte> data)
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
