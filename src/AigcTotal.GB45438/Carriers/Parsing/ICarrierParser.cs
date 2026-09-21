using System.Collections.Generic;
using System.Threading;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Carriers.Parsing
{
    /// <summary>
    /// 容器扫描产出：站点（所有标识出现点）+ 信号（物理事实）+ 通道级检查（发现层 pass/skip/error）。
    /// Schema 层与取证层的检查由管线后段统一合成。
    /// </summary>
    public sealed class CarrierScan
    {
        public CarrierScan(IReadOnlyList<LabelSite> sites, IReadOnlyList<ForensicSignal> signals,
            IReadOnlyList<CheckResult> checks, string? carrierDetail = null)
        {
            Sites = sites;
            Signals = signals;
            Checks = checks;
            CarrierDetail = carrierDetail;
        }

        public IReadOnlyList<LabelSite> Sites { get; }

        public IReadOnlyList<ForensicSignal> Signals { get; }

        /// <summary>该载体的通道级检查结果（发现层：某通道是否发现标识站点）。</summary>
        public IReadOnlyList<CheckResult> Checks { get; }

        /// <summary>探测到的细分形态（MP4 brand、ID3 版本等）；null = 无。</summary>
        public string? CarrierDetail { get; }
    }

    /// <summary>
    /// 容器解析器：枚举载体内全部标识出现点与取证信号。契约：无状态、线程安全；
    /// 只经 BoundedReader 读取；结构畸形/截断抛 CarrierStructureException，超限抛 CarrierLimitException。
    /// </summary>
    public interface ICarrierParser
    {
        CarrierKind Kind { get; }

        /// <summary>从文件头扫描载体，枚举全部标识站点、取证信号与通道级检查。</summary>
        CarrierScan Scan(BoundedReader reader, VerifyOptions options, CancellationToken ct);
    }
}
