using System;
using AigcTotal.Log.Keys;
using Xunit;

namespace AigcTotal.Log.Tests
{
    /// <summary>keys.json 解析校验 + 状态机（active/verify_only/revoked + 吊销时间半开区间语义）。</summary>
    public class KeyStoreAndPolicyTests
    {
        // 载入期 kid↔公钥绑定校验（§2-#3）后，keys.json 必须携带真实可派生 kid 的公钥——
        // 测试一律经 Generate 生成（2020 预创建，避开状态机时间窗）
        private static readonly DateTimeOffset Created =
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static string GeneratedJson(KeyStatus status = KeyStatus.Active,
            DateTimeOffset? revoked = null, string? reason = null)
        {
            GeneratedKey generated = KeyStore.Generate(Created);
            var record = status == KeyStatus.Active
                ? generated.Record
                : new KeyRecord(generated.Record.Kid, generated.Record.Alg, generated.Record.PubkeyJwk,
                    status, Created, Retired: null, Revoked: revoked, RevokedReason: reason);
            return KeyStore.Serialize(KeyFile.Of(record));
        }

        private static readonly DateTimeOffset T =
            new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

        [Fact]
        public void Parse_ValidFile()
        {
            var file = KeyStore.Parse(GeneratedJson());
            var key = Assert.Single(file.Keys);
            Assert.StartsWith("k", key.Kid, StringComparison.Ordinal);
            Assert.Equal(65, key.Kid.Length);
            Assert.Equal("ES256", key.Alg);
            Assert.Equal("EC", key.PubkeyJwk["kty"]);
            Assert.Equal(KeyStatus.Active, key.Status);
            Assert.Null(key.Revoked);
        }

        [Fact]
        public void Parse_KidMustBindToPubkey()
        {
            // 生成后把 kid 改成另一个合法形态 → 绑定校验拒绝（防 keys.json 生成流程错配）
            GeneratedKey generated = KeyStore.Generate(Created);
            string json = KeyStore.Serialize(KeyFile.Of(generated.Record));
            string tamperedKid = "k" + new string('7', 64);
            Assert.Throws<FormatException>(() => KeyStore.Parse(json.Replace(generated.Record.Kid, tamperedKid)));

            // 换成同曲线的另一把真实钥匙的 JWK → 同样不匹配该 kid
            GeneratedKey other = KeyStore.Generate(Created);
            string swapped = json.Replace(generated.Record.PubkeyJwk["x"], other.Record.PubkeyJwk["x"]);
            if (swapped != json)
            {
                Assert.Throws<FormatException>(() => KeyStore.Parse(swapped));
            }
        }

        [Fact]
        public void Generate_Serialize_Parse_Roundtrip()
        {
            GeneratedKey generated = KeyStore.Generate(Created);
            var parsed = KeyStore.Parse(KeyStore.Serialize(KeyFile.Of(generated.Record)));
            KeyRecord key = Assert.Single(parsed.Keys);
            Assert.Equal(generated.Record.Kid, key.Kid);
            Assert.Equal(generated.Record.Alg, key.Alg);
            Assert.Equal(generated.Record.Status, key.Status);
            Assert.Equal(generated.Record.Created, key.Created);
            Assert.Equal(generated.Record.PubkeyJwk["x"], key.PubkeyJwk["x"]);
            Assert.Equal(generated.Record.PubkeyJwk["y"], key.PubkeyJwk["y"]);
        }

        [Fact]
        public void Parse_RejectsMalformed()
        {
            string json = GeneratedJson();
            string kid = KeyStore.Parse(json).Keys[0].Kid;
            // kid 格式
            Assert.Throws<FormatException>(() => KeyStore.Parse(json.Replace(kid, "shortkid")));
            // 非法算法
            Assert.Throws<FormatException>(() => KeyStore.Parse(json.Replace("ES256", "RS256")));
            // 重复 kid（同一记录出现两次）
            int keysAt = json.IndexOf("\"keys\"", StringComparison.Ordinal);
            int firstEntry = json.IndexOf('{', keysAt);
            string entry = json.Substring(firstEntry, json.LastIndexOf(']') - firstEntry);
            Assert.Throws<FormatException>(() => KeyStore.Parse(json.Replace("]", ", " + entry + "]")));
            // revoked 状态缺吊销时间
            string revokedNoTime = json.Replace("\"status\": \"active\"", "\"status\": \"revoked\"");
            Assert.Throws<FormatException>(() => KeyStore.Parse(revokedNoTime));
            // jwk 缺 y
            string noY = System.Text.RegularExpressions.Regex.Replace(
                json, ",\\s*\"y\": \"[A-Za-z0-9_-]+\"", "");
            Assert.Throws<FormatException>(() => KeyStore.Parse(noY));
        }

        private static KeyRecord ActiveRecord() => KeyStore.Generate(Created).Record;

        [Fact]
        public void Policy_ActiveKey_Valid()
        {
            var key = ActiveRecord();
            var result = KeyPolicy.EvaluateForVerification(key, T);
            Assert.True(result.Accepted);
            Assert.False(result.Warn);
        }

        [Fact]
        public void Policy_BeforeCreated_Rejected()
        {
            var key = ActiveRecord();
            // Created = 2020-01-01：早于创建的签名时间必须拒绝
            Assert.False(KeyPolicy.EvaluateForVerification(key, new DateTimeOffset(2019, 12, 31, 23, 59, 59, TimeSpan.Zero)).Accepted);
        }

        [Fact]
        public void Policy_Revoked_Semantics_HalfOpenInterval()
        {
            // 吊销语义：revoked 时刻之后的签名不可信，历史签名仍有效——
            // 验证时以 checkpoint 时间对照：atTime < revoked → 有效+警告；atTime >= revoked → 拒绝
            var key = ActiveRecord() with
            {
                Status = KeyStatus.Revoked,
                Revoked = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero),
                RevokedReason = "key compromise",
            };

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
            var key = ActiveRecord() with { Status = KeyStatus.VerifyOnly };

            var result = KeyPolicy.EvaluateForVerification(key, T);
            Assert.True(result.Accepted);
            Assert.True(result.Warn);
        }
    }
}
