using System.Collections.Generic;
using AigcTotal.GB45438.Carriers;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Verdict
{
    /// <summary>
    /// 聚合判定：多站点一致性、重复打标、信号→取证检查的映射、四档总判定。
    /// 聚合规则表见技术设计 §2.7（冻结）：error > fail > (sites ? (fail? noncompliant : compliant) : not_found)。
    /// </summary>
    public static class VerdictComposer
    {
        /// <summary>取证信号 → 取证检查（M1 基线：CRC 失配 warn；空壳 → error；结构异常 → container_structure error）。</summary>
        public static IEnumerable<CheckResult> ChecksFromSignals(IReadOnlyList<ForensicSignal> signals)
        {
            bool checksum = false, shell = false, padding = false, truncated = false, malformed = false;
            foreach (var signal in signals)
            {
                switch (signal.Kind)
                {
                    case SignalKind.ChecksumMismatch: checksum = true; break;
                    case SignalKind.MetadataShellEmpty: shell = true; break;
                    case SignalKind.AnomalousPadding: padding = true; break;
                    case SignalKind.StructureTruncated: truncated = true; break;
                    case SignalKind.StructureMalformed: malformed = true; break;
                }
            }

            if (checksum)
            {
                yield return new CheckResult(CheckIds.ForensicChecksum, CheckOutcome.Warn,
                    detail: "checksum mismatch detected; label bytes remain readable (baseline policy)");
            }
            if (shell)
            {
                yield return new CheckResult(CheckIds.ForensicWipe, CheckOutcome.Error,
                    code: CheckCodes.StructureMalformed, detail: "metadata shell present but empty");
            }
            if (padding)
            {
                yield return new CheckResult(CheckIds.ForensicPadding, CheckOutcome.Warn);
            }
            if (truncated)
            {
                yield return new CheckResult(CheckIds.ContainerStructure, CheckOutcome.Error,
                    code: CheckCodes.StructureTruncated);
            }
            if (malformed)
            {
                yield return new CheckResult(CheckIds.ContainerStructure, CheckOutcome.Error,
                    code: CheckCodes.StructureMalformed);
            }
        }

        /// <summary>文件级：多站点一致性（FIELDS_DISAGREE）与重复打标（warn）。</summary>
        public static IEnumerable<CheckResult> FileLevelChecks(IReadOnlyList<LabelSite> sites)
        {
            var decoded = new List<IReadOnlyDictionary<string, string>>();
            foreach (var site in sites)
            {
                if (site.Fields != null) decoded.Add(site.Fields);
            }

            if (decoded.Count < 2)
            {
                yield return new CheckResult(CheckIds.FieldsAgree, CheckOutcome.Skip);
                yield return new CheckResult(CheckIds.DuplicateLabel, CheckOutcome.Skip);
                yield break;
            }

            bool allEqual = true;
            var first = decoded[0];
            for (int i = 1; i < decoded.Count; i++)
            {
                if (!SameFields(first, decoded[i]))
                {
                    allEqual = false;
                    break;
                }
            }

            yield return allEqual
                ? new CheckResult(CheckIds.FieldsAgree, CheckOutcome.Pass)
                : new CheckResult(CheckIds.FieldsAgree, CheckOutcome.Fail,
                    detail: $"{decoded.Count} label sites disagree on field values");

            yield return allEqual
                ? new CheckResult(CheckIds.DuplicateLabel, CheckOutcome.Warn,
                    detail: $"{decoded.Count} identical label sites")
                : new CheckResult(CheckIds.DuplicateLabel, CheckOutcome.Skip);
        }

        /// <summary>四档总判定（冻结规则）：任何 error → 无法判定；任何 fail → 不合规；
        /// 有解码站点（或文本提示语站点）→ 合规；否则未检出。</summary>
        public static VerdictKind Compose(IReadOnlyList<CheckResult> checks, IReadOnlyList<LabelSite> sites)
        {
            bool hasError = false, hasFail = false;
            foreach (var check in checks)
            {
                if (check.Outcome == CheckOutcome.Error) hasError = true;
                else if (check.Outcome == CheckOutcome.Fail) hasFail = true;
            }
            if (hasError) return VerdictKind.Inconclusive;
            if (hasFail) return VerdictKind.Noncompliant;
            foreach (var site in sites)
            {
                if (site.Fields != null || site.Encoding == PayloadEncoding.PromptPattern)
                {
                    return VerdictKind.Compliant;
                }
            }
            return VerdictKind.NotFound;
        }

        private static bool SameFields(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var pair in a)
            {
                if (!b.TryGetValue(pair.Key, out string? value) || value != pair.Value) return false;
            }
            return true;
        }
    }
}
