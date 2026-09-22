using System;
using System.Collections.Generic;
using AigcTotal.GB45438;
using AigcTotal.GB45438.Carriers;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.Report
{
    /// <summary>报告信封产出：结构化文档 + canonical 字节形态 + 报告指纹。</summary>
    public sealed class ReportEnvelope
    {
        public ReportEnvelope(Dictionary<string, object?> document, string canonicalJson, string reportSha256)
        {
            Document = document;
            CanonicalJson = canonicalJson;
            ReportSha256 = reportSha256;
        }

        /// <summary>结构化文档（键为信封 schema 的 snake_case 词表）。</summary>
        public Dictionary<string, object?> Document { get; }

        /// <summary>canonical JSON（RFC 8785 受限子集；被签名/入日志的就是这串字节的 UTF-8 形式）。</summary>
        public string CanonicalJson { get; }

        /// <summary>报告指纹：sha256: &lt;canonical 字节的 64 位小写 hex&gt;。</summary>
        public string ReportSha256 { get; }
    }

    /// <summary>
    /// 校验报告信封的官方组装器（schema v1，冻结格式见技术设计 §3.2 / docs/canonicalization.md）：
    /// VerificationResult + 输入文件指纹 → 信封 canonical JSON。
    /// 时间与 report_id 由调用方注入（服务器签发与 CLI 各取所需）；不进 GB45438 库以保持其纯函数性。
    /// </summary>
    public static class AigcReportBuilder
    {
        public const int SchemaVersion = 1;

        public static ReportEnvelope Build(
            VerificationResult result,
            string inputSha256,
            long inputLength,
            string toolName,
            string toolVersion,
            string? reportId = null,
            DateTimeOffset? timestampUtc = null)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (inputSha256 == null) throw new ArgumentNullException(nameof(inputSha256));
            if (toolName == null) throw new ArgumentNullException(nameof(toolName));
            if (toolVersion == null) throw new ArgumentNullException(nameof(toolVersion));

            string id = reportId ?? Ulid.NewUlid();
            if (!Ulid.IsValid(id)) throw new ArgumentException("reportId must be a 26-char ULID", nameof(reportId));
            DateTimeOffset ts = (timestampUtc ?? DateTimeOffset.UtcNow).UtcDateTime;
            string tsText = ts.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

            var document = new Dictionary<string, object?>
            {
                ["version"] = SchemaVersion,
                ["report_id"] = id,
                ["timestamp"] = tsText,
                ["tool"] = new Dictionary<string, object?>
                {
                    ["name"] = toolName,
                    ["version"] = toolVersion,
                },
                ["input"] = BuildInput(result, inputSha256, inputLength),
                ["options"] = new Dictionary<string, object?>
                {
                    ["security"] = new Dictionary<string, object?>
                    {
                        ["max_total_read"] = result.OptionsUsed.MaxTotalRead,
                        ["max_alloc"] = result.OptionsUsed.MaxAlloc,
                        ["max_depth"] = result.OptionsUsed.MaxDepth,
                        ["max_structures"] = result.OptionsUsed.MaxStructures,
                    },
                },
                ["sites"] = BuildSites(result),
                ["signals"] = BuildSignals(result),
                ["checks"] = BuildChecks(result),
                ["verdict"] = VerdictTokens.ToToken(result.Verdict),
            };

            string canonical = CanonicalJson.Serialize(document);
            string fingerprint = "sha256:" + HashHex(System.Text.Encoding.UTF8.GetBytes(canonical));
            return new ReportEnvelope(document, canonical, fingerprint);
        }

        private static Dictionary<string, object?> BuildInput(VerificationResult result,
            string inputSha256, long inputLength)
        {
            var input = new Dictionary<string, object?>
            {
                ["sha256"] = inputSha256,
                ["bytes"] = inputLength,
            };
            if (result.Carrier != null)
            {
                input["mime"] = MimeOf(result.Carrier.Value, result.CarrierDetail);
                input["carrier"] = CarrierNames.ToToken(result.Carrier.Value);
            }
            else
            {
                input["mime"] = null;
                input["carrier"] = null;
            }
            if (result.CarrierDetail != null)
            {
                input["carrier_detail"] = result.CarrierDetail;
            }
            return input;
        }

        private static List<object?> BuildSites(VerificationResult result)
        {
            var sites = new List<object?>();
            foreach (LabelSite site in result.Sites)
            {
                var doc = new Dictionary<string, object?>
                {
                    ["location"] = BuildLocation(site.Location),
                    ["encoding"] = PayloadEncodingTokens.ToToken(site.Encoding),
                };
                if (site.Fields != null)
                {
                    var fields = new Dictionary<string, object?>();
                    foreach (var pair in site.Fields)
                    {
                        fields[pair.Key] = pair.Value;
                    }
                    doc["fields"] = fields;
                }
                else
                {
                    doc["fields"] = null;
                }
                var errors = new List<object?>();
                foreach (string error in site.DecodeErrors)
                {
                    errors.Add(error);
                }
                doc["decode_errors"] = errors;
                sites.Add(doc);
            }
            return sites;
        }

        private static List<object?> BuildSignals(VerificationResult result)
        {
            var signals = new List<object?>();
            foreach (ForensicSignal signal in result.Signals)
            {
                var doc = new Dictionary<string, object?>
                {
                    ["kind"] = SignalKindTokens.ToToken(signal.Kind),
                };
                if (signal.Location != null)
                {
                    doc["location"] = BuildLocation(signal.Location);
                }
                if (signal.Detail != null)
                {
                    doc["detail"] = signal.Detail;
                }
                signals.Add(doc);
            }
            return signals;
        }

        private static List<object?> BuildChecks(VerificationResult result)
        {
            var checks = new List<object?>();
            foreach (CheckResult check in result.Checks)
            {
                var doc = new Dictionary<string, object?>
                {
                    ["check"] = check.Check,
                    ["outcome"] = CheckOutcomeTokens.ToToken(check.Outcome),
                };
                if (check.Site.HasValue)
                {
                    doc["site"] = (long)check.Site.Value;
                }
                if (check.Code != null)
                {
                    doc["code"] = check.Code;
                }
                if (check.Clause != null)
                {
                    var clause = new Dictionary<string, object?> { ["doc"] = check.Clause.Doc };
                    if (check.Clause.Field != null)
                    {
                        clause["field"] = check.Clause.Field;
                    }
                    doc["clause"] = clause;
                }
                if (check.Detail != null)
                {
                    doc["detail"] = check.Detail;
                }
                checks.Add(doc);
            }
            return checks;
        }

        private static Dictionary<string, object?> BuildLocation(SiteLocation location)
        {
            var path = new List<object?>();
            foreach (object element in location.Path)
            {
                if (element is int n) path.Add(n);
                else if (element is long l) path.Add(l);
                else path.Add(element.ToString());
            }
            return new Dictionary<string, object?>
            {
                ["path"] = path,
                ["offset"] = location.Offset,
                ["length"] = location.Length,
            };
        }

        private static string MimeOf(CarrierKind kind, string? detail)
        {
            switch (kind)
            {
                case CarrierKind.Png: return "image/png";
                case CarrierKind.Jpeg: return "image/jpeg";
                case CarrierKind.Mp4: return detail == "qt" ? "video/quicktime" : "video/mp4";
                case CarrierKind.Wav: return "audio/wav"; // IANA 无正式注册，事实标准（docs §3.1-A）
                case CarrierKind.Mp3: return "audio/mpeg";
                case CarrierKind.Text: return "text/plain";
                case CarrierKind.Flac: return "audio/flac";
                case CarrierKind.Ogg: return "audio/ogg";
                case CarrierKind.Avi: return "video/x-msvideo";
                case CarrierKind.Webp: return "image/webp";
                case CarrierKind.Tiff: return "image/tiff";
                case CarrierKind.Gif: return "image/gif";
                case CarrierKind.Ooxml: return "application/octet-stream"; // 家族 MIME 依文档类型而异，carrier 令牌承载语义
                case CarrierKind.Pdf: return "application/pdf";
                default: return "application/octet-stream";
            }
        }

        private static string HashHex(byte[] bytes)
        {
#if NET
            byte[] hash = System.Security.Cryptography.SHA256.HashData(bytes);
#else
            byte[] hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                hash = sha.ComputeHash(bytes);
            }
#endif
            var sb = new System.Text.StringBuilder(64);
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
