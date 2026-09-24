using System;
using System.Collections.Generic;
using System.Text;
using AigcTotal.Log.Keys;
using AigcTotal.Log.Signing;

namespace AigcTotal.Log.Checkpoints
{
    public sealed record CheckpointVerification(bool Valid, string? Error, string? Warning)
    {
        public static CheckpointVerification Ok(string? warning = null) => new CheckpointVerification(true, null, warning);
        public static CheckpointVerification Fail(string error) => new CheckpointVerification(false, error, null);
    }

    /// <summary>
    /// checkpoint 验签：SigningInput canonical 字节 → 按 kid 取 keys.json 公钥 → ES256 验签 →
    /// KeyPolicy 状态机（atTime = checkpoint 自身 timestamp）。与报告验签共用 Es256Verifier 封装。
    /// </summary>
    public static class CheckpointVerifier
    {
        public static CheckpointVerification Verify(Checkpoint checkpoint, KeyFile keys)
        {
            if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            if (checkpoint.TreeHeadSignature == null)
            {
                return CheckpointVerification.Fail("checkpoint is unsigned");
            }

            KeyRecord? key = keys.Find(checkpoint.Kid);
            if (key == null)
            {
                return CheckpointVerification.Fail($"kid '{checkpoint.Kid}' not found in keys.json");
            }

            byte[] spki;
            try
            {
                spki = Es256Wire.BuildSpkiFromJwk(key.PubkeyJwk);
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or FormatException)
            {
                return CheckpointVerification.Fail($"bad pubkey_jwk for kid '{checkpoint.Kid}': {ex.Message}");
            }

            byte[] p1363;
            try
            {
                p1363 = Es256Wire.Base64UrlDecode(checkpoint.TreeHeadSignature);
            }
            catch (FormatException ex)
            {
                return CheckpointVerification.Fail("bad tree_head_signature encoding: " + ex.Message);
            }
            if (p1363.Length != 64)
            {
                return CheckpointVerification.Fail("tree_head_signature must decode to 64 bytes");
            }

            byte[] message = Encoding.UTF8.GetBytes(CheckpointCodec.SigningInput(checkpoint));
            if (!Es256Verifier.Verify(message, p1363, spki))
            {
                return CheckpointVerification.Fail("ES256 verification failed");
            }

            KeyEvaluationResult policy = KeyPolicy.EvaluateForVerification(key, checkpoint.TimestampUtc);
            if (!policy.Accepted)
            {
                return CheckpointVerification.Fail(policy.Reason ?? "key policy rejected");
            }
            return CheckpointVerification.Ok(policy.Warn ? policy.Reason : null);
        }
    }
}
