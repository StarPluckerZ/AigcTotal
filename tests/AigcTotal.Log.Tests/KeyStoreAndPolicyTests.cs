using System;
using AigcTotal.Log.Keys;
using Xunit;

namespace AigcTotal.Log.Tests
{
    /// <summary>keys.json 解析校验 + 状态机（active/verify_only/revoked + 吊销时间半开区间语义）。</summary>
    public class KeyStoreAndPolicyTests
    {
        private const string KidA = "k" + "1111111111111111111111111111111111111111111111111111111111111111";
        private const string KidB = "k" + "2222222222222222222222222222222222222222222222222222222222222222";

        private const string ValidJson = @"
{ ""keys"": [ {
    ""kid"": """ + KidA + @""",
    ""alg"": ""ES256"",
    ""pubkey_jwk"": { ""kty"": ""EC"", ""crv"": ""P-256"", ""x"": ""MKBCTNIcKUSDii11ySs3526iDZ8AiTo7Tu6KPAqv7D4"", ""y"": ""4Etl6SRW2YiLUrN5vfvVHuhp7x8PxltmWWlbbM4IFyM"" },
    ""status"": ""active"",
    ""created"": ""2026-10-01T00:00:00Z"",
    ""retired"": null,
    ""revoked"": null,
    ""revoked_reason"": null
} ] }";

        private static readonly DateTimeOffset T =
            new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

        [Fact]
        public void Parse_ValidFile()
        {
            var file = KeyStore.Parse(ValidJson);
            var key = Assert.Single(file.Keys);
            Assert.Equal(KidA, key.Kid);
            Assert.Equal("ES256", key.Alg);
            Assert.Equal("EC", key.PubkeyJwk["kty"]);
            Assert.Equal(KeyStatus.Active, key.Status);
            Assert.Null(key.Revoked);
        }

        [Fact]
        public void Parse_RejectsMalformed()
        {
            // kid 格式
            Assert.Throws<FormatException>(() => KeyStore.Parse(ValidJson.Replace(KidA, "shortkid")));
            // 非法算法
            Assert.Throws<FormatException>(() => KeyStore.Parse(ValidJson.Replace("ES256", "RS256")));
            // 重复 kid
            const string duplicated = @"{ ""keys"": [
                { ""kid"": """ + KidA + @""", ""alg"": ""ES256"", ""pubkey_jwk"": { ""kty"": ""EC"", ""crv"": ""P-256"", ""x"": ""x"", ""y"": ""y"" }, ""status"": ""active"", ""created"": ""2026-10-01T00:00:00Z"", ""retired"": null, ""revoked"": null, ""revoked_reason"": null },
                { ""kid"": """ + KidA + @""", ""alg"": ""ES256"", ""pubkey_jwk"": { ""kty"": ""EC"", ""crv"": ""P-256"", ""x"": ""x"", ""y"": ""y"" }, ""status"": ""active"", ""created"": ""2026-10-01T00:00:00Z"", ""retired"": null, ""revoked"": null, ""revoked_reason"": null } ] }";
            Assert.Throws<FormatException>(() => KeyStore.Parse(duplicated));
            // revoked 状态缺吊销时间
            const string revokedNoTime = @"{ ""keys"": [ { ""kid"": """ + KidA + @""", ""alg"": ""ES256"",
                ""pubkey_jwk"": { ""kty"": ""EC"", ""crv"": ""P-256"", ""x"": ""x"", ""y"": ""y"" },
                ""status"": ""revoked"", ""created"": ""2026-10-01T00:00:00Z"", ""retired"": null,
                ""revoked"": null, ""revoked_reason"": null } ] }";
            Assert.Throws<FormatException>(() => KeyStore.Parse(revokedNoTime));
            // jwk 缺 y
            Assert.Throws<FormatException>(() => KeyStore.Parse(ValidJson.Replace(
                ", \"y\": \"4Etl6SRW2YiLUrN5vfvVHuhp7x8PxltmWWlbbM4IFyM\"", "")));
        }

        [Fact]
        public void Policy_ActiveKey_Valid()
        {
            var key = KeyStore.Parse(ValidJson).Keys[0];
            var result = KeyPolicy.EvaluateForVerification(key, T);
            Assert.True(result.Accepted);
            Assert.False(result.Warn);
        }

        [Fact]
        public void Policy_BeforeCreated_Rejected()
        {
            var key = KeyStore.Parse(ValidJson).Keys[0];
            Assert.False(KeyPolicy.EvaluateForVerification(key, new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero)).Accepted);
        }

        [Fact]
        public void Policy_Revoked_Semantics_HalfOpenInterval()
        {
            // 吊销语义：revoked 时刻之后的签名不可信，历史签名仍有效——
            // 验证时以 checkpoint 时间对照：atTime < revoked → 有效+警告；atTime >= revoked → 拒绝
            const string revokedJson = @"
{ ""keys"": [ {
    ""kid"": """ + KidB + @""",
    ""alg"": ""ES256"",
    ""pubkey_jwk"": { ""kty"": ""EC"", ""crv"": ""P-256"", ""x"": ""x"", ""y"": ""y"" },
    ""status"": ""revoked"",
    ""created"": ""2026-10-01T00:00:00Z"",
    ""retired"": null,
    ""revoked"": ""2026-10-05T00:00:00Z"",
    ""revoked_reason"": ""key compromise""
} ] }";
            var key = KeyStore.Parse(revokedJson).Keys[0];

            var before = KeyPolicy.EvaluateForVerification(key, new DateTimeOffset(2026, 10, 4, 23, 59, 59, TimeSpan.Zero));
            Assert.True(before.Accepted);
            Assert.True(before.Warn); // 已非 active 钥，验证可用但必须警告

            var atBoundary = KeyPolicy.EvaluateForVerification(key, new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
            Assert.False(atBoundary.Accepted); // 恰在吊销时刻：拒绝（半开区间 [created, revoked)）

            var after = KeyPolicy.EvaluateForVerification(key, new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));
            Assert.False(after.Accepted);
        }

        [Fact]
        public void Policy_VerifyOnly_Valid_WithWarning()
        {
            const string verifyOnly = @"
{ ""keys"": [ { ""kid"": """ + KidB + @""", ""alg"": ""ES256"",
    ""pubkey_jwk"": { ""kty"": ""EC"", ""crv"": ""P-256"", ""x"": ""x"", ""y"": ""y"" },
    ""status"": ""verify_only"", ""created"": ""2026-10-01T00:00:00Z"",
    ""retired"": null, ""revoked"": null, ""revoked_reason"": null } ] }";
            var key = KeyStore.Parse(verifyOnly).Keys[0];

            var result = KeyPolicy.EvaluateForVerification(key, T);
            Assert.True(result.Accepted);
            Assert.True(result.Warn);
        }
    }
}
