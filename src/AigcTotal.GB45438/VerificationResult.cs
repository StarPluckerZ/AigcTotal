using System.Collections.Generic;
using AigcTotal.GB45438.Carriers;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438
{
    /// <summary>验证结果：同版本 + 同输入字节 → 逐字段一致（可复现性主张的基础）。</summary>
    public sealed class VerificationResult
    {
        public VerificationResult(
            CarrierKind? carrier,
            string? carrierDetail,
            SecurityLimits optionsUsed,
            IReadOnlyList<LabelSite> sites,
            IReadOnlyList<ForensicSignal> signals,
            IReadOnlyList<CheckResult> checks,
            VerdictKind verdict)
        {
            Carrier = carrier;
            CarrierDetail = carrierDetail;
            OptionsUsed = optionsUsed;
            Sites = sites;
            Signals = signals;
            Checks = checks;
            Verdict = verdict;
        }

        public CarrierKind? Carrier { get; }

        /// <summary>探测到的细分形态（MP4 brand、ID3 版本等）；null = 未探测到细分。</summary>
        public string? CarrierDetail { get; }

        /// <summary>实际使用的安全限制（可复现性的一部分，进报告 options.security）。</summary>
        public SecurityLimits OptionsUsed { get; }

        public IReadOnlyList<LabelSite> Sites { get; }

        public IReadOnlyList<ForensicSignal> Signals { get; }

        public IReadOnlyList<CheckResult> Checks { get; }

        public VerdictKind Verdict { get; }
    }
}
