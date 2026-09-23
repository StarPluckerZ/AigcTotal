using System;
using System.Collections.Generic;
using System.Text;
using AigcTotal.Log.Merkle;
using Xunit;

namespace AigcTotal.Log.Tests
{
    /// <summary>建树/证明的属性与边界：n=1..64 全 (n,i) 往返、确定性、篡改拒绝、非法输入。</summary>
    public class MerklePropertyTests
    {
        private static byte[] LeafData(int n) => Encoding.UTF8.GetBytes("leaf-" + n);

        [Fact]
        public void Deterministic_SameLeaves_SameRoot()
        {
            var leaves = new List<byte[]>();
            for (int i = 0; i < 17; i++) leaves.Add(Rfc6962.LeafHash(LeafData(i)));

            var a = MerkleTree.FromLeafHashes(leaves);
            var b = MerkleTree.FromLeafHashes(leaves);

            Assert.Equal(a.RootHash, b.RootHash);
        }

        [Fact]
        public void SingleLeaf_RootEqualsLeafHash()
        {
            byte[] leafHash = Rfc6962.LeafHash(LeafData(1));
            var tree = MerkleTree.FromLeafHashes(new[] { leafHash });
            Assert.Equal(leafHash, tree.RootHash);
            Assert.Empty(tree.InclusionPath(0));
        }

        [Fact]
        public void AllSizes_AllIndices_ProofRoundtrip()
        {
            for (int n = 1; n <= 64; n++)
            {
                var leafHashes = new byte[n][];
                for (int i = 0; i < n; i++) leafHashes[i] = Rfc6962.LeafHash(LeafData(i));
                var tree = MerkleTree.FromLeafHashes(leafHashes);

                for (long i = 0; i < n; i++)
                {
                    byte[][] path = tree.InclusionPath(i);
                    Assert.True(MerkleVerifier.VerifyInclusion(leafHashes[i], i, n, path, tree.RootHash),
                        $"roundtrip n={n} i={i}");
                    // 索引错位必须失败
                    long wrong = (i + 1) % n;
                    if (wrong != i)
                    {
                        Assert.False(MerkleVerifier.VerifyInclusion(leafHashes[i], wrong, n, path, tree.RootHash),
                            $"wrong-index n={n} i={i}");
                    }
                }
            }
        }

        [Fact]
        public void Proof_TamperAnyPathElement_IsRejected()
        {
            const int n = 13;
            var leafHashes = new byte[n][];
            for (int i = 0; i < n; i++) leafHashes[i] = Rfc6962.LeafHash(LeafData(i));
            var tree = MerkleTree.FromLeafHashes(leafHashes);

            foreach (long i in new long[] { 0, 5, 12 })
            {
                byte[][] path = tree.InclusionPath(i);
                for (int p = 0; p < path.Length; p++)
                {
                    var tampered = new List<byte[]>(path);
                    var bad = (byte[])path[p].Clone();
                    bad[p % 32] ^= 0x01;
                    tampered[p] = bad;
                    Assert.False(MerkleVerifier.VerifyInclusion(leafHashes[i], i, n, tampered, tree.RootHash),
                        $"tamper element {p} of i={i}");
                }
            }
        }

        [Fact]
        public void Verifier_RejectsBadArguments()
        {
            var leafHashes = new byte[4][];
            for (int i = 0; i < 4; i++) leafHashes[i] = Rfc6962.LeafHash(LeafData(i));
            var tree = MerkleTree.FromLeafHashes(leafHashes);
            byte[][] path = tree.InclusionPath(2);

            // 索引越界
            Assert.False(MerkleVerifier.VerifyInclusion(leafHashes[2], 4, 4, path, tree.RootHash));
            Assert.False(MerkleVerifier.VerifyInclusion(leafHashes[2], -1, 4, path, tree.RootHash));
            // treeSize=0 无意义
            Assert.False(MerkleVerifier.VerifyInclusion(leafHashes[2], 0, 0, path, tree.RootHash));
            // 路径过短（n=4 需 2 元素）
            Assert.False(MerkleVerifier.VerifyInclusion(leafHashes[2], 2, 4, Array.Empty<byte[]>(), tree.RootHash));
            // 路径过长
            var extra = new List<byte[]>(path) { path[0] };
            Assert.False(MerkleVerifier.VerifyInclusion(leafHashes[2], 2, 4, extra, tree.RootHash));
        }

        [Fact]
        public void FromLeafHashes_NullOrWithNullElement_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => MerkleTree.FromLeafHashes(null!));
            Assert.Throws<ArgumentException>(() => MerkleTree.FromLeafHashes(new[] { Rfc6962.LeafHash(LeafData(0)), null! }));
        }
    }
}
