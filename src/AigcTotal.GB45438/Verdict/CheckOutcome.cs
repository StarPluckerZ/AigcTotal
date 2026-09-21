namespace AigcTotal.GB45438.Verdict
{
    /// <summary>检查级结果（五值）。</summary>
    public enum CheckOutcome
    {
        /// <summary>查了且通过。</summary>
        Pass = 1,

        /// <summary>查了且违反（不合规的构成要件）。</summary>
        Fail = 2,

        /// <summary>异常但不影响判定（如 duplicate_label）。</summary>
        Warn = 3,

        /// <summary>不适用（如文件无该通道）。</summary>
        Skip = 4,

        /// <summary>没能查（资源超限/内部异常 → 无法判定）。</summary>
        Error = 5,
    }

    public static class CheckOutcomeTokens
    {
        public static string ToToken(CheckOutcome outcome)
        {
            switch (outcome)
            {
                case CheckOutcome.Pass: return "pass";
                case CheckOutcome.Fail: return "fail";
                case CheckOutcome.Warn: return "warn";
                case CheckOutcome.Skip: return "skip";
                case CheckOutcome.Error: return "error";
                default: return outcome.ToString().ToLowerInvariant();
            }
        }
    }
}
