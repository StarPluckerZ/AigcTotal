using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using AigcTotal.Log.Checkpoints;
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
                new LogEntry(1, Ts, "sha256:" + new string('a', 64)),
                new LogEntry(2, Ts.AddMinutes(1), "sha256:" + new string('b', 64)),
            };
            byte[] root = CheckpointBuilder.RootHashFromEntries(entries);

            // 篡改任一字节（时间戳/序号/哈希）→ 根变化——树对整行提交，而非仅 report_sha256
            var tamperedTs = new List<LogEntry>
            {
                entries[0],
                new LogEntry(2, Ts.AddMinutes(2), entries[1].ReportSha256),
            };
            Assert.NotEqual(root, CheckpointBuilder.RootHashFromEntries(tamperedTs));

            var tamperedSeq = new List<LogEntry> { entries[0], new LogEntry(9, entries[1].TimestampUtc, entries[1].ReportSha256) };
            Assert.NotEqual(root, CheckpointBuilder.RootHashFromEntries(tamperedSeq));

            // 确定性
            Assert.Equal(root, CheckpointBuilder.RootHashFromEntries(entries));
        }

        [Fact]
        public void BuildCheckpoint_SignsCanonicalInput_And_WireRoundtrips()
        {
            // 真 BCL ECDsa 签名端到端：sign(input) → checkpoint → parse → verify（net10 运行时可用）
            var entries = new List<LogEntry>
            {
                new LogEntry(1, Ts, "sha256:" + new string('a', 64)),
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
    }
}
