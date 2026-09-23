using System;
using System.Collections.Generic;

namespace AigcTotal.Log.Checkpoints
{
    /// <summary>
    /// checkpoint 链校验：prev_checkpoint_hash 必须指向前一 checkpoint 的 canonical 字节指纹，
    /// tree_size 与时间严格递增。返回错误列表（空 = 有效）。
    /// </summary>
    public static class CheckpointChain
    {
        public static IReadOnlyList<string> Validate(IReadOnlyList<Checkpoint> chain)
        {
            if (chain == null) throw new ArgumentNullException(nameof(chain));

            var errors = new List<string>();
            for (int i = 1; i < chain.Count; i++)
            {
                Checkpoint prev = chain[i - 1];
                Checkpoint current = chain[i];

                string expected = CheckpointBuilder.FingerprintOf(prev);
                if (!string.Equals(current.PrevCheckpointHash, expected, StringComparison.Ordinal))
                {
                    errors.Add($"checkpoint #{i}: prev_checkpoint_hash does not cover checkpoint #{i - 1}");
                }
                if (current.TreeSize <= prev.TreeSize)
                {
                    errors.Add($"checkpoint #{i}: tree_size must strictly increase");
                }
                if (current.TimestampUtc <= prev.TimestampUtc)
                {
                    errors.Add($"checkpoint #{i}: timestamp must strictly increase");
                }
            }
            return errors;
        }
    }
}
