using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using AigcTotal.Log.Checkpoints;
using AigcTotal.Log.Keys;
using AigcTotal.Log.Segments;
using AigcTotal.Log.Signing;
using Xunit;

namespace AigcTotal.Log.Tests
{
    /// <summary>checkpoint：canonical 字节形态（手工字面量钉死）、签名输入排除签名域、链校验、由段重建。</summary>
    public class CheckpointTests
    {
        private const string Kid = "k0000000000000000000000000000000000000000000000000000000000000000";
        private const string RootHash = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
        private const string PrevHash = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

        private static readonly DateTimeOffset Ts =
            new DateTimeOffset(2026, 9, 21, 8, 30, 0, TimeSpan.Zero);

        [Fact]
        public void SigningInput_CanonicalForm_IsFrozen()
        {
            var cp = new Checkpoint(42, Ts, RootHash, Kid, PrevHash, TreeHeadSignature: null);
            Assert.Equal(
                "{\"kid\":\"" + Kid + "\",\"prev_checkpoint_hash\":\"" + PrevHash + "\"," +
                "\"sha256_root_hash\":\"" + RootHash + "\",\"timestamp\":\"2026-09-21T08:30:00Z\",\"tree_size\":42}",
                CheckpointCodec.SigningInput(cp));
        }

        [Fact]
        public void SigningInput_ExcludesSignature()
        {
            var signed = new Checkpoint(42, Ts, RootHash, Kid, PrevHash, "c2ln");
            Assert.Equal(CheckpointCodec.SigningInput(signed with { TreeHeadSignature = null }),
                CheckpointCodec.SigningInput(signed));
        }

        [Fact]
        public void CanonicalJson_FullForm_IsFrozen()
        {
            // 键序：kid < prev_checkpoint_hash < sha256_root_hash < timestamp < tree_head_signature < tree_size
            var cp = new Checkpoint(42, Ts, RootHash, Kid, PrevHash, "c2lnbmF0dXJl");
            Assert.Equal(
                "{\"kid\":\"" + Kid + "\",\"prev_checkpoint_hash\":\"" + PrevHash + "\"," +
                "\"sha256_root_hash\":\"" + RootHash + "\",\"timestamp\":\"2026-09-21T08:30:00Z\"," +
                "\"tree_head_signature\":\"c2lnbmF0dXJl\",\"tree_size\":42}",
                CheckpointCodec.ToCanonicalJson(cp));
        }

        [Fact]
        public void Parse_Roundtrip_And_Rejects_BadForms()
        {
            var cp = new Checkpoint(42, Ts, RootHash, Kid, null, "c2ln");
            string json = CheckpointCodec.ToCanonicalJson(cp);
            var parsed = CheckpointCodec.Parse(json);
            Assert.Equal(cp, parsed);

            // 非法 kid / 根哈希 / tree_size
            Assert.Throws<FormatException>(() => CheckpointCodec.Parse(
                "{\"kid\":\"x\",\"sha256_root_hash\":\"" + RootHash + "\",\"timestamp\":\"2026-09-21T08:30:00Z\",\"tree_size\":42}"));
            Assert.Throws<FormatException>(() => CheckpointCodec.Parse(
                "{\"kid\":\"" + Kid + "\",\"sha256_root_hash\":\"deadbeef\",\"timestamp\":\"2026-09-21T08:30:00Z\",\"tree_size\":42}"));
            Assert.Throws<FormatException>(() => CheckpointCodec.Parse(
                "{\"kid\":\"" + Kid + "\",\"sha256_root_hash\":\"" + RootHash + "\",\"timestamp\":\"2026-09-21T08:30:00Z\",\"tree_size\":-1}"));
            // 未知字段拒绝（canonical 形态封闭性）
            Assert.Throws<FormatException>(() => CheckpointCodec.Parse(
                "{\"extra\":1,\"kid\":\"" + Kid + "\",\"sha256_root_hash\":\"" + RootHash + "\"," +
                "\"timestamp\":\"2026-09-21T08:30:00Z\",\"tree_size\":42}"));
        }

        [Fact]
        public void Chain_Validates_Linkage_Monotonicity()
        {
            var c1 = new Checkpoint(10, Ts, RootHash, Kid, null, "sig1");
            string c1Json = CheckpointCodec.ToCanonicalJson(c1);
            string prevHash = "sha256:" + Hex(sha256(Encoding.UTF8.GetBytes(c1Json)));
            var c2 = new Checkpoint(20, Ts.AddHours(1), RootHash, Kid, prevHash, "sig2");
            string c2Json = CheckpointCodec.ToCanonicalJson(c2);
            string prev2 = "sha256:" + Hex(sha256(Encoding.UTF8.GetBytes(c2Json)));
            var c3 = new Checkpoint(30, Ts.AddHours(2), RootHash, Kid, prev2, "sig3");

            Assert.Empty(CheckpointChain.Validate(new[] { c1, c2, c3 }));

            // 断链：prev 哈希不指向前一 checkpoint 的 canonical 字节
            var broken = new Checkpoint(30, Ts.AddHours(2), RootHash, Kid, "sha256:" + new string('f', 64), "sig3");
            Assert.Single(CheckpointChain.Validate(new[] { c1, c2, broken }));

            // tree_size 不严格递增
            var dupSize = new Checkpoint(20, Ts.AddHours(2), RootHash, Kid, prev2, "sig3");
            Assert.NotEmpty(CheckpointChain.Validate(new[] { c1, c2, dupSize }));

            // 时间回拨
            var backwards = new Checkpoint(30, Ts, RootHash, Kid, prev2, "sig3");
            Assert.NotEmpty(CheckpointChain.Validate(new[] { c1, c2, backwards }));
        }

        [Fact]
        public void RootFromEntries_CommitsToEveryByte_OfEntryLines()
        {
            var entries = new List<LogEntry>
            {
                new LogEntry(1, Ts, "sha256:" + new string('c', 64), "sha256:" + new string('a', 64)),
                new LogEntry(2, Ts.AddMinutes(1), "sha256:" + new string('d', 64), "sha256:" + new string('b', 64)),
            };
            byte[] root = CheckpointBuilder.RootHashFromEntries(entries);

            // 篡改任一字节（时间戳/序号/任一哈希）→ 根变化——树对整行提交，而非仅 report_sha256
            var tamperedTs = new List<LogEntry>
            {
                entries[0],
                new LogEntry(2, Ts.AddMinutes(2), entries[1].InputSha256, entries[1].ReportSha256),
            };
            Assert.NotEqual(root, CheckpointBuilder.RootHashFromEntries(tamperedTs));

            var tamperedSeq = new List<LogEntry> { entries[0], new LogEntry(9, entries[1].TimestampUtc, entries[1].InputSha256, entries[1].ReportSha256) };
            Assert.NotEqual(root, CheckpointBuilder.RootHashFromEntries(tamperedSeq));

            var tamperedInput = new List<LogEntry>
            {
                entries[0],
                new LogEntry(2, entries[1].TimestampUtc, "sha256:" + new string('e', 64), entries[1].ReportSha256),
            };
            Assert.NotEqual(root, CheckpointBuilder.RootHashFromEntries(tamperedInput));

            // 确定性
            Assert.Equal(root, CheckpointBuilder.RootHashFromEntries(entries));
        }

        [Fact]
        public void BuildCheckpoint_SignsCanonicalInput_And_WireRoundtrips()
        {
            // 真 BCL ECDsa 签名端到端：sign(input) → checkpoint → parse → verify（net10 运行时可用）
            var entries = new List<LogEntry>
            {
                new LogEntry(1, Ts, "sha256:" + new string('c', 64), "sha256:" + new string('a', 64)),
            };
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var signer = new BclP256Signer(key);

            byte[] root = CheckpointBuilder.RootHashFromEntries(entries);
            Checkpoint cp = CheckpointBuilder.BuildCheckpoint(entries, treeSize: 1,
                timestampUtc: Ts, kid: Kid, prevCheckpointHash: null, signer: signer);

            Assert.Equal("sha256:" + Hex(root), cp.Sha256RootHash);
            string input = CheckpointCodec.SigningInput(cp);
            byte[] sig1 = cp.TreeHeadSignature != null
                ? Es256Wire.Base64UrlDecode(cp.TreeHeadSignature)
                : throw new InvalidOperationException("unsigned");
            Assert.True(key.VerifyData(Encoding.UTF8.GetBytes(input), sig1, HashAlgorithmName.SHA256));

            // 解析往返 + 验证端（同钥）接受
            var parsed = CheckpointCodec.Parse(CheckpointCodec.ToCanonicalJson(cp));
            byte[] sig = Es256Wire.Base64UrlDecode(parsed.TreeHeadSignature!);
            Assert.True(key.VerifyData(Encoding.UTF8.GetBytes(CheckpointCodec.SigningInput(parsed)), sig, HashAlgorithmName.SHA256));
        }

        private static byte[] sha256(byte[] data) => System.Security.Cryptography.SHA256.HashData(data);
        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        // —— CheckpointVerifier（W3-③）：签名域验签 + 密钥状态机 ——

        private static (KeyFile Keys, System.Security.Cryptography.ECDsa Key) NewKey()
        {
            var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            byte[] spki = key.ExportSubjectPublicKeyInfo();
            string kid = Es256Wire.KidFromSpki(spki);
            byte[] point = spki.AsSpan(26).ToArray();
            var record = new AigcTotal.Log.Keys.KeyRecord(kid, "ES256",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kty"] = "EC",
                    ["crv"] = "P-256",
                    ["x"] = Es256Wire.Base64UrlEncode(point.AsSpan(1, 32)),
                    ["y"] = Es256Wire.Base64UrlEncode(point.AsSpan(33, 32)),
                },
                AigcTotal.Log.Keys.KeyStatus.Active,
                new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null, null);
            return (AigcTotal.Log.Keys.KeyFile.Of(record), key);
        }

        private static Checkpoint SignedCheckpoint(System.Security.Cryptography.ECDsa key, string? prev = null)
        {
            string kid = Es256Wire.KidFromSpki(key.ExportSubjectPublicKeyInfo());
            return CheckpointBuilder.BuildCheckpoint(
                new[] { new LogEntry(1, Ts, "sha256:" + new string('c', 64), "sha256:" + new string('a', 64)) },
                treeSize: 1, timestampUtc: Ts, kid: kid, prevCheckpointHash: prev, signer: new BclP256Signer(key));
        }

        [Fact]
        public void Verifier_Accepts_ValidCheckpoint()
        {
            var (keys, key) = NewKey();
            var verify = CheckpointVerifier.Verify(SignedCheckpoint(key), keys);
            Assert.True(verify.Valid);
            Assert.Null(verify.Error);
        }

        [Fact]
        public void Verifier_Rejects_TamperedRootAndForeignKid()
        {
            var (keys, key) = NewKey();
            Checkpoint cp = SignedCheckpoint(key);

            // 篡改根哈希 → 签名不再覆盖 SigningInput
            string root = cp.Sha256RootHash;
            var badRoot = cp with { Sha256RootHash = string.Concat("sha256:0", root.AsSpan(8)) };
            Assert.False(CheckpointVerifier.Verify(badRoot, keys).Valid);

            // keys.json 不含该 kid
            var (otherKeys, otherKey) = NewKey();
            var notFound = CheckpointVerifier.Verify(cp, otherKeys);
            Assert.False(notFound.Valid);
            Assert.Contains("not found", notFound.Error, StringComparison.Ordinal);

            // kid 对得上但公钥是另一把（验签失败路径；KeyFile.Of 不做绑定校验，绑定校验在 Parse）
            var wrongPubkey = AigcTotal.Log.Keys.KeyFile.Of(otherKeys.Keys[0] with { Kid = cp.Kid });
            var badSig = CheckpointVerifier.Verify(cp, wrongPubkey);
            Assert.False(badSig.Valid);
            Assert.Contains("ES256 verification failed", badSig.Error, StringComparison.Ordinal);

            // 未签名 checkpoint
            Assert.False(CheckpointVerifier.Verify(cp with { TreeHeadSignature = null }, keys).Valid);

            // 另一把钥匙签的 checkpoint 用本 keys.json 验（走 not found）
            Assert.False(CheckpointVerifier.Verify(SignedCheckpoint(otherKey), keys).Valid);
        }

        [Fact]
        public void Verifier_RevokedKey_Semantics()
        {
            var (keys, key) = NewKey();
            Checkpoint cp = SignedCheckpoint(key);

            // checkpoint 时间早于吊销 → 有效 + 警告；晚于 → 拒绝（半开区间）
            var revokedEarly = AigcTotal.Log.Keys.KeyFile.Of(keys.Keys[0] with
            {
                Status = AigcTotal.Log.Keys.KeyStatus.Revoked,
                Revoked = Ts.AddHours(1),
            });
            Checkpoint atTs = cp; // Ts 早于 revoked
            var early = CheckpointVerifier.Verify(atTs, revokedEarly);
            Assert.True(early.Valid);
            Assert.NotNull(early.Warning);

            Checkpoint late = CheckpointBuilder.BuildCheckpoint(
                new[] { new LogEntry(1, Ts, "sha256:" + new string('c', 64), "sha256:" + new string('a', 64)) },
                treeSize: 1, timestampUtc: Ts.AddHours(2),
                kid: cp.Kid, prevCheckpointHash: null, signer: new BclP256Signer(key));
            Assert.False(CheckpointVerifier.Verify(late, revokedEarly).Valid);
        }

        [Fact]
        public void Chain_HeadWithPrev_IsFlagged_Truncated()
        {
            // §2-#2：链首带 prev = 截断链——audit 无外部锚时必须失败
            var c0 = new Checkpoint(10, Ts, RootHash, Kid, "sha256:" + new string('e', 64), "sig0");
            var errors = CheckpointChain.Validate(new[] { c0 });
            Assert.Single(errors);
            Assert.Contains("truncated chain", errors[0], StringComparison.Ordinal);

            var genesis = new Checkpoint(10, Ts, RootHash, Kid, null, "sig0");
            Assert.Empty(CheckpointChain.Validate(new[] { genesis }));
        }
    }
}
