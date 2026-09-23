using System;
using System.Collections.Generic;

namespace AigcTotal.Log.Merkle
{
    /// <summary>
    /// RFC 6962 §2.1 Merkle 树哈希（k-split 语义）：n≥2 时 k = 小于 n 的最大 2 的幂，
    /// 根 = NodeHash(MTH(D[0:k]), MTH(D[k:n]))——奇数末段独立成子树，不做复制上卷。
    /// 包含证明按同一定义递归生成（§2.1.3 PATH），与 §2.1.2 验证算法配套。
    /// 不可变；叶哈希列表留存以支持证明生成（服务器侧可另缓存子树哈希，接口不变）。
    /// </summary>
    public sealed class MerkleTree
    {
        private readonly byte[][] _leafHashes;
        private readonly byte[] _rootHash;

        private MerkleTree(byte[][] leafHashes, byte[] rootHash)
        {
            _leafHashes = leafHashes;
            _rootHash = rootHash;
        }

        public long LeafCount => _leafHashes.Length;

        public byte[] RootHash => (byte[])_rootHash.Clone();

        /// <summary>叶哈希均为 RFC 6962 叶哈希（32 字节）。空列表合法（空树根 = SHA-256(空串)）。</summary>
        public static MerkleTree FromLeafHashes(IReadOnlyList<byte[]> leafHashes)
        {
            if (leafHashes == null) throw new ArgumentNullException(nameof(leafHashes));
            foreach (byte[] leaf in leafHashes)
            {
                if (leaf == null) throw new ArgumentException("leaf hash must not be null", nameof(leafHashes));
                if (leaf.Length != 32) throw new ArgumentException("leaf hash must be 32 bytes", nameof(leafHashes));
            }

            var leaves = new byte[leafHashes.Count][];
            for (int i = 0; i < leafHashes.Count; i++) leaves[i] = (byte[])leafHashes[i]!.Clone();
            byte[] root = leafHashes.Count == 0 ? Rfc6962.EmptyHash() : SubtreeHash(leaves, 0, leaves.Length);
            return new MerkleTree(leaves, root);
        }

        /// <summary>生成第 index 叶到根的审计路径（RFC 6962 §2.1.3 PATH；自叶向根排列，每元素 32 字节）。</summary>
        public byte[][] InclusionPath(long index)
        {
            if (index < 0 || index >= LeafCount) throw new ArgumentOutOfRangeException(nameof(index));
            var path = new List<byte[]>();
            BuildPath(index, 0, _leafHashes.Length, path);
            return path.ToArray();
        }

        private void BuildPath(long index, long lo, long hi, List<byte[]> path)
        {
            if (hi - lo == 1) return;
            long k = LargestPowerOfTwoBelow(hi - lo);
            if (index < k)
            {
                BuildPath(index, lo, lo + k, path);
                path.Add(SubtreeHash(_leafHashes, lo + k, hi));
            }
            else
            {
                BuildPath(index - k, lo + k, hi, path);
                path.Add(SubtreeHash(_leafHashes, lo, lo + k));
            }
        }

        private static byte[] SubtreeHash(byte[][] leaves, long lo, long hi)
        {
            if (hi - lo == 1) return leaves[lo];
            long k = LargestPowerOfTwoBelow(hi - lo);
            return Rfc6962.NodeHash(SubtreeHash(leaves, lo, lo + k), SubtreeHash(leaves, lo + k, hi));
        }

        private static long LargestPowerOfTwoBelow(long n)
        {
            long k = 1;
            while (k * 2 < n) k *= 2;
            return k;
        }
    }
}
