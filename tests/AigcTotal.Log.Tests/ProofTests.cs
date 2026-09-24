using System;
using System.Collections.Generic;
using System.Linq;
using AigcTotal.Log.Merkle;
using AigcTotal.Log.Proofs;
using AigcTotal.Log.Segments;
using AigcTotal.Log.Signing;
using Xunit;

namespace AigcTotal.Log.Tests
{
    /// <summary>proof.json：codec 往返与封闭性、生成/验证全 (n, i) 矩阵、篡改拒绝。</summary>
    public class ProofTests
    {
        private static readonly DateTimeOffset Ts = new(2026, 9, 24, 8, 30, 0, TimeSpan.Zero);

        private static List<LogEntry> Entries(int n) =>
            Enumerable.Range(0, n)
                .Select(i => new LogEntry(
                    Sequence: 100 + i, // 非零基序号：证明下标换算 seq − baseSeq
                    TimestampUtc: Ts.AddMinutes(i),
                    InputSha256: "sha256:" + new string((char)('a' + i % 26), 64),
                    ReportSha256: "sha256:" + new string((char)('A' + i % 26), 64)))
                .ToList();

        // 32 字节定长（audit_path 元素的真实形态；元素长度由 codec 强制）
        private static readonly string[] TwoPathElements =
            { Es256Wire.Base64UrlEncode(new byte[32]), Es256Wire.Base64UrlEncode(new byte[32]) };

        [Fact]
        public void Codec_Roundtrip_And_FrozenKeyOrder()
        {
            var proof = new InclusionProof(42, 100, TwoPathElements);
            string json = ProofCodec.Serialize(proof);
            Assert.Equal("{\"audit_path\":[\"" + TwoPathElements[0] + "\",\"" + TwoPathElements[1]
                + "\"],\"seq\":42,\"tree_size\":100}", json);

            var parsed = ProofCodec.Parse(json);
            Assert.Equal(42, parsed.Seq);
            Assert.Equal(100, parsed.TreeSize);
            Assert.Equal(proof.AuditPath, parsed.AuditPath);
        }

        [Fact]
        public void Parse_Rejects_MalformedForms()
        {
            // 未知字段 / 元素非字符串 / 非 32 字节 / seq 越界
            Assert.Throws<FormatException>(() => ProofCodec.Parse(
                "{\"extra\":1,\"seq\":1,\"tree_size\":2,\"audit_path\":[]}"));
            Assert.Throws<FormatException>(() => ProofCodec.Parse(
                "{\"audit_path\":[42],\"seq\":0,\"tree_size\":1}"));
            Assert.Throws<FormatException>(() => ProofCodec.Parse(
                "{\"audit_path\":[\"AAAA\"],\"seq\":0,\"tree_size\":1}")); // 3 字节
            Assert.Throws<FormatException>(() => ProofCodec.Parse(
                "{\"audit_path\":[],\"seq\":-1,\"tree_size\":1}")); // seq 为负（全局序号非负）
            Assert.Throws<FormatException>(() => ProofCodec.Parse(
                "{\"audit_path\":[],\"seq\":0,\"tree_size\":0}")); // tree_size 非正
            Assert.Throws<FormatException>(() => ProofCodec.Parse(
                "{\"audit_path\":[\"A\"],\"seq\":0,\"tree_size\":1}")); // 含填充的 base64
        }

        [Fact]
        public void GenerateThenVerify_AllSizes_AllIndices()
        {
            for (int n = 1; n <= 9; n++)
            {
                List<LogEntry> entries = Entries(n);
                byte[] root = Checkpoints.CheckpointBuilder.RootHashFromEntries(entries);
                long baseSeq = entries[0].Sequence;

                for (int i = 0; i < n; i++)
                {
                    InclusionProof proof = ProofFactory.Generate(entries, entries[i].Sequence);
                    Assert.Equal(n, proof.TreeSize);
                    Assert.Equal(entries[i].Sequence, proof.Seq);
                    Assert.True(ProofChecker.Verify(entries[i], proof, baseSeq, root), $"n={n} i={i}");
                }
            }
        }

        [Fact]
        public void Verify_Rejects_Tampering()
        {
            List<LogEntry> entries = Entries(6);
            byte[] root = Checkpoints.CheckpointBuilder.RootHashFromEntries(entries);
            long baseSeq = entries[0].Sequence;
            LogEntry entry = entries[3];
            InclusionProof proof = ProofFactory.Generate(entries, entry.Sequence);

            // 换成另一条目的证明（seq 与叶不匹配）
            Assert.False(ProofChecker.Verify(entries[2], proof, baseSeq, root));
            // 篡改路径元素
            var tamperedPath = proof.AuditPath.ToArray();
            tamperedPath[0] = tamperedPath[0].Length > 1
                ? (tamperedPath[0][0] == 'A' ? 'B' : 'A') + tamperedPath[0].Substring(1)
                : tamperedPath[0];
            Assert.False(ProofChecker.Verify(entry, proof with { AuditPath = tamperedPath }, baseSeq, root));
            // 篡改根
            byte[] badRoot = (byte[])root.Clone();
            badRoot[0] ^= 1;
            Assert.False(ProofChecker.Verify(entry, proof, baseSeq, badRoot));
            // 注：tree_size ↔ root 的绑定由 checkpoint 对 (sha256_root_hash, tree_size) 成立，
            // CLI 侧 FindCheckpointByTreeSize 已钉死两者同源；ProofChecker 不重复认证该绑定
            //（RFC 9162 验证算法本身不独立认证 tree_size，这是设计而非缺陷）。
        }

        [Fact]
        public void Generate_OutOfRange_Throws()
        {
            List<LogEntry> entries = Entries(3);
            Assert.Throws<ArgumentOutOfRangeException>(() => ProofFactory.Generate(entries, 99));
            Assert.Throws<ArgumentOutOfRangeException>(() => ProofFactory.Generate(entries, 99 + 4));
        }

        [Fact]
        public void AuditPath_Matches_MerkleTreePath()
        {
            // proof 的 audit_path 必须与 MerkleTree.InclusionPath 逐字节一致（同一 RFC 6962 定义）
            List<LogEntry> entries = Entries(7);
            var leafHashes = entries
                .Select(e => Rfc6962.LeafHash(System.Text.Encoding.UTF8.GetBytes(e.ToCanonicalLine())))
                .ToList();
            var tree = MerkleTree.FromLeafHashes(leafHashes);
            long baseSeq = entries[0].Sequence;

            for (int i = 0; i < entries.Count; i++)
            {
                InclusionProof proof = ProofFactory.Generate(entries, entries[i].Sequence);
                byte[][] expected = tree.InclusionPath(i);
                Assert.Equal(expected.Length, proof.AuditPath.Count);
                for (int p = 0; p < expected.Length; p++)
                {
                    Assert.Equal(expected[p], Es256Wire.Base64UrlDecode(proof.AuditPath[p]));
                }
            }
        }
    }
}
