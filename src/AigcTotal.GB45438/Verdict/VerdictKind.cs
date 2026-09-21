namespace AigcTotal.GB45438.Verdict
{
    /// <summary>四档总判定（只增不改的机器词表；中文为展示层）。</summary>
    public enum VerdictKind
    {
        /// <summary>合规：有标识且符合附录 E。</summary>
        Compliant = 1,

        /// <summary>不合规：有标识但违反附录 E / 站点间冲突。</summary>
        Noncompliant = 2,

        /// <summary>未检出：无标识且无被动过痕迹。</summary>
        NotFound = 3,

        /// <summary>无法判定：容器损坏 / 探测歧义 / 擦除证据 / 资源超限。</summary>
        Inconclusive = 4,
    }

    public static class VerdictTokens
    {
        public static string ToToken(VerdictKind verdict)
        {
            switch (verdict)
            {
                case VerdictKind.Compliant: return "compliant";
                case VerdictKind.Noncompliant: return "noncompliant";
                case VerdictKind.NotFound: return "not_found";
                case VerdictKind.Inconclusive: return "inconclusive";
                default: return verdict.ToString().ToLowerInvariant();
            }
        }
    }
}
