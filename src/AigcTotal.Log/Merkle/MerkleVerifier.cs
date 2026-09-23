using System;
using System.Collections.Generic;

namespace AigcTotal.Log.Merkle
{
    /// <summary>
    /// 包含证明验证：RFC 9162 §2.1.3.2 官方算法逐字实现（fn/sn 的 LSB 形式），
    /// 与 RFC 6962/9162 §2.1.3.1 的 k-split 生成定义配套。
    /// </summary>
    public static class MerkleVerifier
    {
        public static bool VerifyInclusion(byte[] leafHash, long leafIndex, long treeSize,
            IReadOnlyList<byte[]> auditPath, byte[] rootHash)
        {
            if (leafHash == null || rootHash == null || auditPath == null) return false;
            if (treeSize <= 0 || leafIndex < 0 || leafIndex >= treeSize) return false;
            foreach (byte[] p in auditPath)
            {
                if (p == null || p.Length != 32) return false;
            }

            // RFC 9162 §2.1.3.2：
            // 2. fn = leaf_index，sn = tree_size - 1；r = leaf_hash
            long fn = leafIndex;
            long sn = treeSize - 1;
            byte[] r = (byte[])leafHash.Clone();

            foreach (byte[] p in auditPath)
            {
                // 4.a. sn 为 0 仍有路径元素 → 失败（路径过长）
                if (sn == 0) return false;

                bool lsbSet = (fn & 1) == 1;
                if (lsbSet || fn == sn)
                {
                    // 4.b.i. p 为左兄弟
                    r = Rfc6962.NodeHash(p, r);
                    if (!lsbSet)
                    {
                        // 4.b.ii. fn 与 sn 同步右移，直至 LSB(fn) 置位或 fn 为 0
                        while (fn > 0 && (fn & 1) == 0)
                        {
                            fn >>= 1;
                            sn >>= 1;
                        }
                    }
                }
                else
                {
                    // 4.b.(otherwise) p 为右兄弟
                    r = Rfc6962.NodeHash(r, p);
                }

                // 4.c. fn 与 sn 各右移一位
                fn >>= 1;
                sn >>= 1;
            }

            // 5. sn 归零且 r 等于根
            return sn == 0 && BytesEqual(r, rootHash);
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }
    }
}
