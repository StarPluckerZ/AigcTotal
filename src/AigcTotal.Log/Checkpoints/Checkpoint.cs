using System;
using System.Collections.Generic;
using AigcTotal.Log.Segments;
using AigcTotal.Report;

namespace AigcTotal.Log.Checkpoints
{
    /// <summary>
    /// 签名 checkpoint（CT STH 词表 + prev_checkpoint_hash 链字段）。
    /// TreeHeadSignature = base64url(ES256 P1363 64 字节)，签名域 = SigningInput（不含签名字段的 canonical JSON）。
    /// prev_checkpoint_hash = sha256(前一 checkpoint 的完整 canonical JSON 字节)——链自哈希：改写历史段
    /// ⇒ 重建树根变 ⇒ 与已发布 checkpoint 冲突。
    /// </summary>
    public sealed record Checkpoint(
        long TreeSize,
        DateTimeOffset TimestampUtc,
        string Sha256RootHash,
        string Kid,
        string? PrevCheckpointHash,
        string? TreeHeadSignature)
    {
    }

    public static class CheckpointCodec
    {
        public static string SigningInput(Checkpoint checkpoint)
        {
            return CanonicalJson.Serialize(ToDictionary(checkpoint, includeSignature: false));
        }

        public static string ToCanonicalJson(Checkpoint checkpoint)
        {
            return CanonicalJson.Serialize(ToDictionary(checkpoint, includeSignature: true));
        }

        public static Checkpoint Parse(string json)
        {
            Dictionary<string, object?> doc;
            try
            {
                doc = CanonicalJson.Deserialize(json);
            }
            catch (FormatException ex)
            {
                throw new FormatException("malformed checkpoint json: " + ex.Message, ex);
            }

            foreach (string key in doc.Keys)
            {
                if (key != "kid" && key != "prev_checkpoint_hash" && key != "sha256_root_hash"
                    && key != "timestamp" && key != "tree_size" && key != "tree_head_signature")
                {
                    throw new FormatException($"unknown checkpoint field '{key}'");
                }
            }
            if (doc["kid"] is not string kid || doc["sha256_root_hash"] is not string root
                || doc["timestamp"] is not string ts || doc["tree_size"] is not long treeSize)
            {
                throw new FormatException("missing or mistyped required field");
            }
            if (!TokenFormat.IsValidKid(kid)) throw new FormatException("bad kid format");
            if (!TokenFormat.IsValidSha256Claim(root)) throw new FormatException("bad sha256_root_hash format");
            if (treeSize < 0) throw new FormatException("tree_size must be non-negative");
            if (!LogTime.TryParse(ts, out DateTimeOffset timestamp)) throw new FormatException("bad timestamp");

            string? prev = doc.TryGetValue("prev_checkpoint_hash", out object? prevObj) ? prevObj as string : null;
            if (prev != null && !TokenFormat.IsValidSha256Claim(prev))
            {
                throw new FormatException("bad prev_checkpoint_hash format");
            }
            string? sig = doc.TryGetValue("tree_head_signature", out object? sigObj) ? sigObj as string : null;

            return new Checkpoint(treeSize, timestamp, root, kid, prev, sig);
        }

        private static Dictionary<string, object?> ToDictionary(Checkpoint checkpoint, bool includeSignature)
        {
            var doc = new Dictionary<string, object?>
            {
                ["kid"] = checkpoint.Kid,
                ["sha256_root_hash"] = checkpoint.Sha256RootHash,
                ["timestamp"] = LogTime.Format(checkpoint.TimestampUtc),
                ["tree_size"] = checkpoint.TreeSize,
            };
            if (checkpoint.PrevCheckpointHash != null)
            {
                doc["prev_checkpoint_hash"] = checkpoint.PrevCheckpointHash;
            }
            if (includeSignature && checkpoint.TreeHeadSignature != null)
            {
                doc["tree_head_signature"] = checkpoint.TreeHeadSignature;
            }
            return doc;
        }
    }
}
