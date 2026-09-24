using System;
using System.Collections.Generic;
using System.Text;
using AigcTotal.Log.Merkle;
using AigcTotal.Log.Segments;
using AigcTotal.Report;

namespace AigcTotal.Log.Proofs
{
    /// <summary>
    /// 包含证明（proof.json，技术设计 §3.2）：{"seq":N,"tree_size":M,"audit_path":["base64url",…]}。
    /// seq 为日志全局序号；tree_size 为签发该证明的 checkpoint 的树大小；audit_path 自叶向根（RFC 6962 §2.1.3）。
    /// proof 是便利物——验证者也可自行从公开段重建树计算路径（叶子在全树中的下标 = seq − 全日志首条序号）。
    /// </summary>
    public sealed record InclusionProof(long Seq, long TreeSize, IReadOnlyList<string> AuditPath);

    public static class ProofCodec
    {
        public static string Serialize(InclusionProof proof)
        {
            if (proof == null) throw new ArgumentNullException(nameof(proof));
            var path = new List<object?>();
            foreach (string element in proof.AuditPath)
            {
                path.Add(element);
            }
            return CanonicalJson.Serialize(new Dictionary<string, object?>
            {
                ["audit_path"] = path,
                ["seq"] = proof.Seq,
                ["tree_size"] = proof.TreeSize,
            });
        }

        /// <summary>严格解析：字段封闭（audit_path/seq/tree_size）、每元素 32 字节、seq 在树内。</summary>
        public static InclusionProof Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            Dictionary<string, object?> doc;
            try
            {
                doc = CanonicalJson.Deserialize(json);
            }
            catch (FormatException ex)
            {
                throw new FormatException("malformed proof json: " + ex.Message, ex);
            }
            if (doc.Count != 3
                || !doc.TryGetValue("audit_path", out object? pathObj) || pathObj is not List<object?> path
                || !doc.TryGetValue("seq", out object? seqObj) || seqObj is not long seq
                || !doc.TryGetValue("tree_size", out object? sizeObj) || sizeObj is not long treeSize)
            {
                throw new FormatException("proof must have exactly audit_path/seq/tree_size");
            }
            if (treeSize <= 0) throw new FormatException("tree_size must be positive");
            // seq 是日志全局序号（非树内下标）：非负即可；下标边界 = seq − baseSeq 由 ProofChecker 判定
            if (seq < 0) throw new FormatException("seq must be non-negative");

            var elements = new List<string>();
            foreach (object? item in path)
            {
                if (item is not string element)
                {
                    throw new FormatException("audit_path elements must be strings");
                }
                byte[] decoded;
                try
                {
                    decoded = Signing.Es256Wire.Base64UrlDecode(element);
                }
                catch (FormatException ex)
                {
                    throw new FormatException("bad audit_path element encoding: " + ex.Message, ex);
                }
                if (decoded.Length != 32)
                {
                    throw new FormatException("audit_path elements must decode to 32 bytes");
                }
                elements.Add(element);
            }
            return new InclusionProof(seq, treeSize, elements);
        }
    }

    /// <summary>
    /// 证明生成（编排器在 checkpoint 时点补齐）：对全树（截至该 checkpoint 的全部条目，按 seq 升序）
    /// 生成第 seq 条的审计路径。
    /// </summary>
    public static class ProofFactory
    {
        public static InclusionProof Generate(IReadOnlyList<LogEntry> entriesInOrder, long seq)
        {
            if (entriesInOrder == null) throw new ArgumentNullException(nameof(entriesInOrder));
            if (entriesInOrder.Count == 0) throw new ArgumentException("entries must not be empty", nameof(entriesInOrder));

            long baseSeq = entriesInOrder[0].Sequence;
            long index = seq - baseSeq;
            if (index < 0 || index >= entriesInOrder.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(seq), "seq not covered by the given entries");
            }

            var leafHashes = new List<byte[]>(entriesInOrder.Count);
            foreach (LogEntry entry in entriesInOrder)
            {
                leafHashes.Add(Rfc6962.LeafHash(Encoding.UTF8.GetBytes(entry.ToCanonicalLine())));
            }
            byte[][] path = MerkleTree.FromLeafHashes(leafHashes).InclusionPath(index);

            var encoded = new List<string>(path.Length);
            foreach (byte[] node in path)
            {
                encoded.Add(Signing.Es256Wire.Base64UrlEncode(node));
            }
            return new InclusionProof(seq, entriesInOrder.Count, encoded);
        }
    }

    /// <summary>
    /// 证明验证：叶哈希 = Rfc6962.LeafHash(条目 canonical 行)，下标 = seq − baseSeq，
    /// 对照给定树根（来自对应 tree_size 的 checkpoint 的 sha256_root_hash）。
    /// </summary>
    public static class ProofChecker
    {
        public static bool Verify(LogEntry entry, InclusionProof proof, long baseSeq, byte[] rootHash)
        {
            if (entry == null || proof == null || rootHash == null) return false;
            if (entry.Sequence != proof.Seq) return false;
            long index = proof.Seq - baseSeq;
            if (index < 0) return false;

            var path = new List<byte[]>(proof.AuditPath.Count);
            foreach (string element in proof.AuditPath)
            {
                try
                {
                    path.Add(Signing.Es256Wire.Base64UrlDecode(element));
                }
                catch (FormatException)
                {
                    return false;
                }
            }
            byte[] leafHash = Rfc6962.LeafHash(Encoding.UTF8.GetBytes(entry.ToCanonicalLine()));
            return MerkleVerifier.VerifyInclusion(leafHash, index, proof.TreeSize, path, rootHash);
        }
    }
}
