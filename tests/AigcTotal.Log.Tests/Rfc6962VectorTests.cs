using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AigcTotal.Log.Merkle;
using Xunit;

namespace AigcTotal.Log.Tests
{
    /// <summary>
    /// RFC 6962 官方向量（源自 google/certificate-transparency 的 ctcrypto 测试，
    /// 经 C++/Python/Go 三方实现互证）：叶/节点域分离哈希、空树、n=1..8 树头、
    /// 3,630,887 叶真实规模包含证明。
    /// </summary>
    public class Rfc6962VectorTests
    {
        private static string VectorPath(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestVectors", name);

        private static string[] ReadLines(string name) =>
            File.ReadAllLines(VectorPath(name)).Where(l => l.Trim().Length > 0).ToArray();

        private static byte[] Hex(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

        [Fact]
        public void EmptyTreeHash_MatchesVector()
        {
            string expected = ReadLines("rfc6962_empty_tree_hash.txt")[0];
            // 空树根 = SHA-256(空串)（RFC 6962 §2.1.1），由 MerkleTree.FromLeafHashes(空) 产出
            var tree = MerkleTree.FromLeafHashes(Array.Empty<byte[]>());
            Assert.Equal(expected, Hex(tree.RootHash));
        }

        [Fact]
        public void LeafHash_DomainSeparated_MatchesVectors()
        {
            // 向量文件逐行: <input_hex> <hash_hex>（空输入行为单列）
            foreach (string line in ReadLines("rfc6962_leaf_hash_vectors.txt"))
            {
                string[] parts = line.Split(' ');
                byte[] input = parts[0] == "-" ? Array.Empty<byte>() : Hex(parts[0]);
                Assert.Equal(parts[1], Hex(Rfc6962.LeafHash(input)));
            }
        }

        [Fact]
        public void NodeHash_DomainSeparated_MatchesVector()
        {
            string[] lines = ReadLines("rfc6962_node_hash_vectors.txt");
            string[] parts = lines[0].Split(' ');
            Assert.Equal(parts[2], Hex(Rfc6962.NodeHash(Hex(parts[0]), Hex(parts[1]))));
        }

        [Fact]
        public void TreeHeads_MatchVectors_ForAllPrefixSizes()
        {
            string[] leaves = ReadLines("rfc6962_tree_head_leaves.txt");
            string[] roots = ReadLines("rfc6962_tree_head_roots.txt");
            Assert.Equal(8, roots.Length);

            for (int n = 1; n <= 8; n++)
            {
                // "-" 为空字节串叶（向量集的第一个叶即空串）
                var leafHashes = leaves.Take(n)
                    .Select(l => Rfc6962.LeafHash(l == "-" ? Array.Empty<byte>() : Hex(l)))
                    .ToArray();
                var tree = MerkleTree.FromLeafHashes(leafHashes);
                Assert.Equal(roots[n - 1], Hex(tree.RootHash));
            }
        }

        [Fact]
        public void InclusionProof_GeneratedPaths_VerifyAgainstVectorRoots()
        {
            // 生成端：n=1..8 全部 (n, index) 组合的证明必须被验证端接受，且对向量树根成立
            string[] leaves = ReadLines("rfc6962_tree_head_leaves.txt");
            string[] roots = ReadLines("rfc6962_tree_head_roots.txt");

            for (int n = 1; n <= 8; n++)
            {
                // "-" 为空字节串叶（向量集的第一个叶即空串）
                var leafHashes = leaves.Take(n)
                    .Select(l => Rfc6962.LeafHash(l == "-" ? Array.Empty<byte>() : Hex(l)))
                    .ToArray();
                var tree = MerkleTree.FromLeafHashes(leafHashes);
                for (long i = 0; i < n; i++)
                {
                    byte[][] path = tree.InclusionPath(i);
                    Assert.True(MerkleVerifier.VerifyInclusion(leafHashes[i], i, n, path, tree.RootHash),
                        $"self-check n={n} i={i}");
                }
            }
        }

        [Fact]
        public void InclusionProof_RealWorldVector_Verifies()
        {
            // 3,630,887 叶 / 索引 848,049 / 22 元素审计路径——验证端的独立权威检查
            string[] lines = ReadLines("rfc6962_inclusion_vector.txt");
            // 行1: leaf_index tree_size path_length
            string[] head = lines[0].Split(' ');
            long index = long.Parse(head[0], System.Globalization.CultureInfo.InvariantCulture);
            long treeSize = long.Parse(head[1], System.Globalization.CultureInfo.InvariantCulture);
            int pathLen = int.Parse(head[2], System.Globalization.CultureInfo.InvariantCulture);
            byte[] leaf = Hex(lines[1]); // 叶原文（证书条目字节）的 hex
            byte[] root = Hex(lines[2]);
            var path = new List<byte[]>();
            for (int i = 0; i < pathLen; i++) path.Add(Hex(lines[3 + i]));

            Assert.Equal(22, pathLen);
            Assert.True(MerkleVerifier.VerifyInclusion(Rfc6962.LeafHash(leaf), index, treeSize, path, root));
        }

        [Fact]
        public void InclusionProof_TamperedPathOrLeaf_IsRejected()
        {
            string[] lines = ReadLines("rfc6962_inclusion_vector.txt");
            string[] head = lines[0].Split(' ');
            long index = long.Parse(head[0], System.Globalization.CultureInfo.InvariantCulture);
            long treeSize = long.Parse(head[1], System.Globalization.CultureInfo.InvariantCulture);
            byte[] leaf = Hex(lines[1]);
            byte[] root = Hex(lines[2]);
            var path = new List<byte[]>();
            for (int i = 3; i < lines.Length; i++) path.Add(Hex(lines[i]));

            byte[] leafHash = Rfc6962.LeafHash(leaf);

            // 错根
            byte[] badRoot = (byte[])root.Clone();
            badRoot[0] ^= 0xFF;
            Assert.False(MerkleVerifier.VerifyInclusion(leafHash, index, treeSize, path, badRoot));
            // 错叶
            byte[] badLeaf = (byte[])leaf.Clone();
            badLeaf[0] ^= 0xFF;
            Assert.False(MerkleVerifier.VerifyInclusion(Rfc6962.LeafHash(badLeaf), index, treeSize, path, root));
            // 路径换位
            var swapped = new List<byte[]>(path) { [0] = path[1], [1] = path[0] };
            Assert.False(MerkleVerifier.VerifyInclusion(leafHash, index, treeSize, swapped, root));
            // 错索引（同树另一位置）
            Assert.False(MerkleVerifier.VerifyInclusion(leafHash, index + 1, treeSize, path, root));
        }
    }
}
