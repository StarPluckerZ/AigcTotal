namespace AigcTotal.GB45438.Verdict
{
    /// <summary>取证信号：扫描到的物理事实（证据，不评价）；取证类 check 评估信号后给结论。只增不改。</summary>
    public enum SignalKind
    {
        /// <summary>元数据结构壳在、内容空（XMP 包壳空、AIGC chunk 空）。</summary>
        MetadataShellEmpty = 1,

        /// <summary>异常填充（ID3 padding、chunk 间零填充块）。</summary>
        AnomalousPadding = 2,

        /// <summary>校验不符（PNG chunk CRC 等）。</summary>
        ChecksumMismatch = 3,

        /// <summary>容器截断。</summary>
        StructureTruncated = 4,

        /// <summary>结构畸形。</summary>
        StructureMalformed = 5,

        /// <summary>资源超限（读满上限 / 深度爆表 / 结构数爆表）。</summary>
        ResourceLimitExceeded = 6,

        /// <summary>探测歧义（多探测器命中）。</summary>
        CarrierAmbiguous = 7,
    }

    public static class SignalKindTokens
    {
        public static string ToToken(SignalKind kind)
        {
            switch (kind)
            {
                case SignalKind.MetadataShellEmpty: return "metadata_shell_empty";
                case SignalKind.AnomalousPadding: return "anomalous_padding";
                case SignalKind.ChecksumMismatch: return "checksum_mismatch";
                case SignalKind.StructureTruncated: return "structure_truncated";
                case SignalKind.StructureMalformed: return "structure_malformed";
                case SignalKind.ResourceLimitExceeded: return "resource_limit_exceeded";
                case SignalKind.CarrierAmbiguous: return "carrier_ambiguous";
                default: return kind.ToString().ToLowerInvariant();
            }
        }
    }
}
