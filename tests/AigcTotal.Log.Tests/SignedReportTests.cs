using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using AigcTotal.Log.Keys;
using AigcTotal.Log.Signing;
using Xunit;

namespace AigcTotal.Log.Tests
{
    /// <summary>SignedReport：外层格式冻结、签名域=信封 canonical 字节、验签状态机、篡改拒绝。</summary>
    public class SignedReportTests
    {
        private static readonly DateTimeOffset Created =
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static (KeyFile Keys, System.Security.Cryptography.ECDsa Key, string Kid) NewKey()
        {
            var key = System.Security.Cryptography.ECDsa.Create(
                System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            byte[] spki = key.ExportSubjectPublicKeyInfo();
            string kid = Es256Wire.KidFromSpki(spki);
            byte[] point = spki.AsSpan(26).ToArray();
            var record = new KeyRecord(kid, "ES256",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kty"] = "EC",
                    ["crv"] = "P-256",
                    ["x"] = Es256Wire.Base64UrlEncode(point.AsSpan(1, 32)),
                    ["y"] = Es256Wire.Base64UrlEncode(point.AsSpan(33, 32)),
                },
                KeyStatus.Active, Created, null, null, null);
            return (KeyFile.Of(record), key, kid);
        }

        private static Dictionary<string, object?> SampleEnvelope(string timestamp = "2026-09-24T08:30:00Z") => new()
        {
            ["version"] = 1L,
            ["report_id"] = "01J8GZ3X9QF7Y3N4R5T6Z8A9BC",
            ["timestamp"] = timestamp,
            ["input"] = new Dictionary<string, object?>
            {
                ["sha256"] = "sha256:" + new string('c', 64),
                ["bytes"] = 181L,
            },
            ["verdict"] = "compliant",
        };

        [Fact]
        public void OuterForm_IsFrozen()
        {
            // 外层 {"report":…,"signatures":[{alg,kid,value}]}；键序 report < signatures；
            // 签名 value 以真实签名填充，此处钉死除 value 外的全部结构字节
            var (keys, key, kid) = NewKey();
            var envelope = SampleEnvelope();
            var signed = SignedReportCodec.Sign(envelope, kid, new BclP256Signer(key));

            Assert.StartsWith("{\"report\":{\"input\":{\"bytes\":181,", signed.CanonicalJson, StringComparison.Ordinal);
            Assert.EndsWith("},\"signatures\":[{\"alg\":\"ES256\",\"kid\":\"" + kid
                + "\",\"value\":\"" + signed.Signatures[0].Value + "\"}]}", signed.CanonicalJson, StringComparison.Ordinal);
        }

        [Fact]
        public void SigningDomain_IsEnvelopeCanonicalBytes()
        {
            var (keys, key, kid) = NewKey();
            var envelope = SampleEnvelope();
            var signed = SignedReportCodec.Sign(envelope, kid, new BclP256Signer(key));

            byte[] domain = signed.EnvelopeCanonicalBytes();
            Assert.Equal(AigcTotal.Report.CanonicalJson.Serialize(envelope),
                System.Text.Encoding.UTF8.GetString(domain));

            // 签名确实覆盖信封 canonical 字节（同钥同域可验证）
            Assert.True(key.VerifyData(domain, signed.Signatures[0].DecodeValue(), HashAlgorithmName.SHA256));
        }

        [Fact]
        public void ReportSha256_IsEnvelopeCanonicalSha256()
        {
            var (keys, key, kid) = NewKey();
            var signed = SignedReportCodec.Sign(SampleEnvelope(), kid, new BclP256Signer(key));

            string expected = "sha256:" + Convert.ToHexString(
                SHA256.HashData(signed.EnvelopeCanonicalBytes())).ToLowerInvariant();
            Assert.Equal(expected, signed.ReportSha256());
        }

        [Fact]
        public void Verify_ValidSignature_Accepts()
        {
            var (keys, key, kid) = NewKey();
            var signed = SignedReportCodec.Sign(SampleEnvelope(), kid, new BclP256Signer(key));

            ReportVerifier.Result result = ReportVerifier.Verify(signed, keys);

            Assert.True(result.Valid);
            Assert.Empty(result.Errors);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void Verify_WrongKey_Rejects()
        {
            var (keys, key, kid) = NewKey();
            var signed = SignedReportCodec.Sign(SampleEnvelope(), kid, new BclP256Signer(key));
            var (otherKeys, _, _) = NewKey(); // keys.json 只含另一把钥匙

            ReportVerifier.Result result = ReportVerifier.Verify(signed, otherKeys);

            Assert.False(result.Valid);
            Assert.Contains(result.Errors, e => e.Contains("not found in keys.json"));
        }

        [Fact]
        public void Verify_TamperedEnvelope_Rejects()
        {
            var (keys, key, kid) = NewKey();
            var signed = SignedReportCodec.Sign(SampleEnvelope(), kid, new BclP256Signer(key));

            // 篡改信封一个字节（verdict）→ 签名不再覆盖
            var tampered = SignedReportCodec.Parse(signed.CanonicalJson.Replace("\"compliant\"", "\"complianX\""));
            var result = ReportVerifier.Verify(tampered, keys);

            Assert.False(result.Valid);
            Assert.Contains(result.Errors, e => e.Contains("ES256 verification failed"));
            Assert.NotEqual(signed.ReportSha256(), tampered.ReportSha256());
        }

        [Fact]
        public void Verify_VerifyOnlyKey_Warns()
        {
            var (keys, key, kid) = NewKey();
            var signed = SignedReportCodec.Sign(SampleEnvelope(), kid, new BclP256Signer(key));
            var verifyOnly = KeyFile.Of(keys.Keys[0] with { Status = KeyStatus.VerifyOnly });

            ReportVerifier.Result result = ReportVerifier.Verify(signed, verifyOnly);

            Assert.True(result.Valid);
            Assert.Contains(result.Warnings, w => w.Contains("verify_only"));
        }

        [Fact]
        public void Parse_Rejects_MalformedForms()
        {
            var (_, key, kid) = NewKey();
            string good = SignedReportCodec.Sign(SampleEnvelope(), kid, new BclP256Signer(key)).CanonicalJson;

            // 顶层混入未知字段
            Assert.Throws<FormatException>(() => SignedReportCodec.Parse(
                good.Replace("{\"report\":", "{\"extra\":1,\"report\":")));
            // 空签名数组
            int cut = good.IndexOf(",\"signatures\"", StringComparison.Ordinal);
            Assert.Throws<FormatException>(() => SignedReportCodec.Parse(
                string.Concat(good.AsSpan(0, cut), ",\"signatures\":[]}")));
            // 非 ES256
            Assert.Throws<FormatException>(() => SignedReportCodec.Parse(
                good.Replace("\"ES256\"", "\"RS256\"")));
            // 签名值非 64 字节（截断 base64url）
            int cut2 = good.LastIndexOf('"', good.Length - 3) - 4;
            string shortSig = string.Concat(good.AsSpan(0, cut2), "\"}");
            Assert.Throws<FormatException>(() => SignedReportCodec.Parse(shortSig));
        }

        [Fact]
        public void Parse_Roundtrip_KeepsSignatures()
        {
            var (keys, key, kid) = NewKey();
            var signed = SignedReportCodec.Sign(SampleEnvelope(), kid, new BclP256Signer(key));

            var parsed = SignedReportCodec.Parse(signed.CanonicalJson);
            Assert.Equal(signed.ReportSha256(), parsed.ReportSha256());
            Assert.Equal(signed.Signatures, parsed.Signatures);
            Assert.True(ReportVerifier.Verify(parsed, keys).Valid);
        }
    }
}
