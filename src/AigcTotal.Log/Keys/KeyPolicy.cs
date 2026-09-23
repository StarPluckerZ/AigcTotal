using System;

namespace AigcTotal.Log.Keys
{
    public sealed record KeyEvaluationResult(bool Accepted, bool Warn, string? Reason)
    {
        public static KeyEvaluationResult Valid() => new KeyEvaluationResult(true, false, null);
        public static KeyEvaluationResult ValidWithWarning(string reason) => new KeyEvaluationResult(true, true, reason);
        public static KeyEvaluationResult Reject(string reason) => new KeyEvaluationResult(false, false, reason);
    }

    /// <summary>
    /// 验证时的密钥状态机（签发侧状态约束由 server 负责）：
    /// 吊销语义为半开区间 [created, revoked)——revoked 时刻之后的签名不可信，恰在 revoked 时刻即拒绝；
    /// 历史签名（checkpoint 时间早于 revoked）仍有效但必须警告（密钥已非 active）。
    /// </summary>
    public static class KeyPolicy
    {
        public static KeyEvaluationResult EvaluateForVerification(KeyRecord key, DateTimeOffset atTime)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (key.Alg != "ES256") return KeyEvaluationResult.Reject("unsupported alg");
            if (atTime < key.Created) return KeyEvaluationResult.Reject("used before created");

            if (key.Status == KeyStatus.Revoked)
            {
                if (key.Revoked == null) return KeyEvaluationResult.Reject("revoked without timestamp");
                if (atTime >= key.Revoked.Value) return KeyEvaluationResult.Reject("key revoked");
                return KeyEvaluationResult.ValidWithWarning("key is revoked (signature predates revocation)");
            }
            if (key.Status == KeyStatus.VerifyOnly)
            {
                return KeyEvaluationResult.ValidWithWarning("key is verify_only");
            }
            if (key.Retired != null && atTime >= key.Retired.Value)
            {
                return KeyEvaluationResult.ValidWithWarning("key is past retirement time");
            }
            return KeyEvaluationResult.Valid();
        }
    }
}
