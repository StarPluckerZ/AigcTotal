using System;
using System.Collections.Generic;
using System.Text;
using AigcTotal.Log.Keys;
using AigcTotal.Log.Segments;
using AigcTotal.Report;

namespace AigcTotal.Log.Signing
{
    /// <summary>单条 ES256 签名（value = base64url(64 字节 P1363)）。</summary>
    public sealed record ReportSignature(string Alg, string Kid, string Value)
    {
        public byte[] DecodeValue() => Es256Wire.Base64UrlDecode(Value);
    }

    /// <summary>
    /// 签名报告（外层信封）：{"report":&lt;报告信封&gt;,"signatures":[{"alg":"ES256","kid":"k…","value":"…"}]}。
    /// 签名域 = 报告信封的 canonical 字节（外层不参与签名，天然排除 signatures 数组自身）。
    /// 格式 v1 冻结（docs/transparency-log.md §6）；SM2 双签 = signatures 追加项（后议）。
    /// </summary>
    public sealed class SignedReport
    {
        internal SignedReport(Dictionary<string, object?> envelope, IReadOnlyList<ReportSignature> signatures, string canonicalJson)
        {
            Envelope = envelope;
            Signatures = signatures;
            CanonicalJson = canonicalJson;
        }

        /// <summary>报告信封文档（schema v1 词表）。</summary>
        public Dictionary<string, object?> Envelope { get; }

        public IReadOnlyList<ReportSignature> Signatures { get; }

        /// <summary>外层 canonical JSON（交付形态的精确字节）。</summary>
        public string CanonicalJson { get; }

        /// <summary>签名域：信封 canonical 字节——所有签名与 report_sha256 都对这串字节生效。</summary>
        public byte[] EnvelopeCanonicalBytes() =>
            Encoding.UTF8.GetBytes(global::AigcTotal.Report.CanonicalJson.Serialize(Envelope));

        /// <summary>报告指纹（= 信封 canonical 字节的 SHA-256，`sha256:` 形态）——与日志条目对照的值。</summary>
        public string ReportSha256()
        {
            byte[] digest = Rfc6962Sha256(EnvelopeCanonicalBytes());
            var sb = new StringBuilder(7 + 64);
            sb.Append("sha256:");
            foreach (byte b in digest)
            {
                sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static byte[] Rfc6962Sha256(byte[] data)
        {
#if NET
            return System.Security.Cryptography.SHA256.HashData(data);
#else
            using var sha = System.Security.Cryptography.SHA256.Create();
            return sha.ComputeHash(data);
#endif
        }
    }

    /// <summary>组装（写侧）与解析（读侧）。解析严格封闭：未知字段、非 ES256、坏 kid/签名形态一律 FormatException。</summary>
    public static class SignedReportCodec
    {
        /// <summary>以信封文档 + 已有签名组装（签名域 = 信封 canonical 字节，由签名方先行计算）。</summary>
        public static SignedReport Compose(Dictionary<string, object?> envelope, IReadOnlyList<ReportSignature> signatures)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            if (signatures == null) throw new ArgumentNullException(nameof(signatures));
            foreach (ReportSignature sig in signatures)
            {
                ValidateSignature(sig);
            }

            var sigList = new List<object?>();
            foreach (ReportSignature sig in signatures)
            {
                sigList.Add(new Dictionary<string, object?>
                {
                    ["alg"] = sig.Alg,
                    ["kid"] = sig.Kid,
                    ["value"] = sig.Value,
                });
            }
            var doc = new Dictionary<string, object?>
            {
                ["report"] = envelope,
                ["signatures"] = sigList,
            };
            return new SignedReport(envelope, signatures, CanonicalJson.Serialize(doc));
        }

        /// <summary>便捷单签名组装：对信封 canonical 字节签名（IReportSigner 契约：输出 64 字节 P1363）。</summary>
        public static SignedReport Sign(Dictionary<string, object?> envelope, string kid, IReportSigner signer)
        {
            if (signer == null) throw new ArgumentNullException(nameof(signer));
            if (!TokenFormat.IsValidKid(kid)) throw new ArgumentException("bad kid format", nameof(kid));
            byte[] canonical = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(envelope));
            byte[] p1363 = signer.Sign(canonical);
            if (p1363.Length != 64) throw new InvalidOperationException("signer must return 64-byte P1363");
            return Compose(envelope, new[] { new ReportSignature("ES256", kid, Es256Wire.Base64UrlEncode(p1363)) });
        }

        public static SignedReport Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            Dictionary<string, object?> doc;
            try
            {
                doc = CanonicalJson.Deserialize(json);
            }
            catch (FormatException ex)
            {
                throw new FormatException("malformed signed report: " + ex.Message, ex);
            }
            if (doc.Count != 2 || !(doc.ContainsKey("report") && doc.ContainsKey("signatures")))
            {
                throw new FormatException("signed report must contain exactly 'report' and 'signatures'");
            }
            if (doc["report"] is not Dictionary<string, object?> envelope)
            {
                throw new FormatException("'report' must be an object");
            }
            if (doc["signatures"] is not List<object?> sigList)
            {
                throw new FormatException("'signatures' must be an array");
            }

            var signatures = new List<ReportSignature>();
            foreach (object? item in sigList)
            {
                if (item is not Dictionary<string, object?> sigDoc)
                {
                    throw new FormatException("signature entry must be an object");
                }
                if (sigDoc.Count != 3
                    || !sigDoc.TryGetValue("alg", out object? algObj) || algObj is not string alg
                    || !sigDoc.TryGetValue("kid", out object? kidObj) || kidObj is not string kid
                    || !sigDoc.TryGetValue("value", out object? valueObj) || valueObj is not string value)
                {
                    throw new FormatException("signature entry must have exactly alg/kid/value (strings)");
                }
                var sig = new ReportSignature(alg, kid, value);
                ValidateSignature(sig);
                signatures.Add(sig);
            }
            if (signatures.Count == 0)
            {
                throw new FormatException("signed report must carry at least one signature");
            }
            return new SignedReport(envelope, signatures, CanonicalJson.Serialize(doc));
        }

        private static void ValidateSignature(ReportSignature sig)
        {
            if (sig.Alg != "ES256") throw new FormatException("signature alg must be ES256");
            if (!TokenFormat.IsValidKid(sig.Kid)) throw new FormatException("bad kid format");
            byte[] decoded;
            try
            {
                decoded = sig.DecodeValue();
            }
            catch (FormatException ex)
            {
                throw new FormatException("bad signature value encoding: " + ex.Message, ex);
            }
            if (decoded.Length != 64)
            {
                throw new FormatException("signature value must decode to 64 bytes (P1363)");
            }
        }
    }

    /// <summary>
    /// 报告签名验证器（全链五步的前两步：canonical 重算 → ES256 验签 + 密钥状态机）。
    /// report_sha256 与日志/包含证明的对照由调用方（aigc-verify）在其后接续。
    /// </summary>
    public static class ReportVerifier
    {
        public sealed record Result(bool Valid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
        {
            internal Result(bool valid, List<string> errors, List<string> warnings)
                : this(valid, errors.AsReadOnly(), warnings.AsReadOnly())
            {
            }
        }

        /// <summary>
        /// 验证签名报告：对每条签名，按 kid 取 keys.json 公钥验签（atTime = 信封 timestamp，
        /// 缺失/不可解析 → 记错误并跳过状态机）；任一签名有效即通过（多重签名/轮换期语义）。
        /// </summary>
        public static Result Verify(SignedReport report, KeyFile keys)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (keys == null) throw new ArgumentNullException(nameof(keys));

            var errors = new List<string>();
            var warnings = new List<string>();
            byte[] message = report.EnvelopeCanonicalBytes();

            DateTimeOffset? atTime = null;
            if (report.Envelope.TryGetValue("timestamp", out object? tsObj) && tsObj is string ts
                && LogTime.TryParse(ts, out DateTimeOffset parsed))
            {
                atTime = parsed;
            }
            else
            {
                errors.Add("envelope has no parsable 'timestamp' for key policy evaluation");
            }

            bool anyValid = false;
            foreach (ReportSignature sig in report.Signatures)
            {
                string sigLabel = "signature(kid=" + sig.Kid + ")";
                KeyRecord? key = keys.Find(sig.Kid);
                if (key == null)
                {
                    errors.Add($"{sigLabel}: kid not found in keys.json");
                    continue;
                }

                byte[] spki;
                try
                {
                    spki = Es256Wire.BuildSpkiFromJwk(key.PubkeyJwk);
                }
                catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or FormatException)
                {
                    errors.Add($"{sigLabel}: bad pubkey_jwk ({ex.Message})");
                    continue;
                }

                byte[] p1363 = sig.DecodeValue();
                if (!Es256Verifier.Verify(message, p1363, spki))
                {
                    errors.Add($"{sigLabel}: ES256 verification failed");
                    continue;
                }

                if (atTime != null)
                {
                    KeyEvaluationResult policy = KeyPolicy.EvaluateForVerification(key, atTime.Value);
                    if (!policy.Accepted)
                    {
                        errors.Add($"{sigLabel}: {policy.Reason}");
                        continue;
                    }
                    if (policy.Warn && policy.Reason != null)
                    {
                        warnings.Add($"{sigLabel}: {policy.Reason}");
                    }
                }
                anyValid = true;
            }
            if (!anyValid && errors.Count == 0)
            {
                errors.Add("no usable signature");
            }
            return new Result(anyValid, errors, warnings);
        }
    }
}
