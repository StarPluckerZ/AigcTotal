using System.Collections.Generic;

namespace AigcTotal.GB45438.Verdict
{
    /// <summary>条款引用：doc 为官方编号（TC260-PG-… / GB 45438-2025 / 国信办通字〔2025〕2号）。</summary>
    public sealed class ClauseReference
    {
        public ClauseReference(string doc, string? field = null)
        {
            Doc = doc;
            Field = field;
        }

        public string Doc { get; }

        public string? Field { get; }
    }

    /// <summary>一条原子检查规则的执行结果。site 为 sites 数组索引（null = 文件级检查）。</summary>
    public sealed class CheckResult
    {
        public CheckResult(string check, CheckOutcome outcome, int? site = null,
            string? code = null, ClauseReference? clause = null, string? detail = null)
        {
            Check = check;
            Outcome = outcome;
            Site = site;
            Code = code;
            Clause = clause;
            Detail = detail;
        }

        public string Check { get; }

        public CheckOutcome Outcome { get; }

        public int? Site { get; }

        public string? Code { get; }

        public ClauseReference? Clause { get; }

        public string? Detail { get; }
    }

    /// <summary>取证信号实例。</summary>
    public sealed class ForensicSignal
    {
        public ForensicSignal(SignalKind kind, Carriers.SiteLocation? location = null, string? detail = null)
        {
            Kind = kind;
            Location = location;
            Detail = detail;
        }

        public SignalKind Kind { get; }

        public Carriers.SiteLocation? Location { get; }

        public string? Detail { get; }
    }
}
