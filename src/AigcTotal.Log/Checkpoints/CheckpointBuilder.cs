using System;
using System.Collections.Generic;
using System.Text;
using AigcTotal.Log.Merkle;
using AigcTotal.Log.Segments;
using AigcTotal.Log.Signing;

namespace AigcTotal.Log.Checkpoints
{
    /// <summary>由段条目建树与签发 checkpoint。树叶 = 条目 canonical 行字节——树对段内每个字节提交。</summary>
    public static class CheckpointBuilder
    {
        public static byte[] RootHashFromEntries(IEnumerable<LogEntry> entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            var leafHashes = new List<byte[]>();
            foreach (LogEntry entry in entries)
            {
                leafHashes.Add(Rfc6962.LeafHash(Encoding.UTF8.GetBytes(entry.ToCanonicalLine())));
            }
            return MerkleTree.FromLeafHashes(leafHashes).RootHash;
        }

        /// <summary>建树 → 签名 SigningInput → 产出带 tree_head_signature 的 checkpoint。</summary>
        public static Checkpoint BuildCheckpoint(IEnumerable<LogEntry> entries, long treeSize,
            DateTimeOffset timestampUtc, string kid, string? prevCheckpointHash, IReportSigner signer)
        {
            if (signer == null) throw new ArgumentNullException(nameof(signer));
            if (!TokenFormat.IsValidKid(kid)) throw new ArgumentException("bad kid format", nameof(kid));
            if (prevCheckpointHash != null && !TokenFormat.IsValidSha256Claim(prevCheckpointHash))
            {
                throw new ArgumentException("bad prev_checkpoint_hash format", nameof(prevCheckpointHash));
            }

            string rootHash = ToClaim(RootHashFromEntries(entries));
            var unsigned = new Checkpoint(treeSize, timestampUtc, rootHash, kid, prevCheckpointHash, TreeHeadSignature: null);
            byte[] signature = signer.Sign(Encoding.UTF8.GetBytes(CheckpointCodec.SigningInput(unsigned)));
            return unsigned with { TreeHeadSignature = Es256Wire.Base64UrlEncode(signature) };
        }

        /// <summary>checkpoint 指纹（链校验用）：sha256(完整 canonical JSON 字节) 的 "sha256:" 形式。</summary>
        public static string FingerprintOf(Checkpoint checkpoint)
        {
            return ToClaim(Rfc6962.Sha256(Encoding.UTF8.GetBytes(CheckpointCodec.ToCanonicalJson(checkpoint))));
        }

        internal static string ToClaim(byte[] digest)
        {
            var sb = new StringBuilder(7 + 64);
            sb.Append("sha256:");
            foreach (byte b in digest) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
