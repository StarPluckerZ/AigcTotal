using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AigcTotal.Log.Checkpoints;
using AigcTotal.Log.Keys;
using AigcTotal.Log.Proofs;
using AigcTotal.Log.Segments;
using AigcTotal.Log.Signing;
using Xunit;

namespace AigcTotal.NetStandard.Tests
{
    /// <summary>
    /// AigcTotal.Log netstandard2.0 产物的行为冒烟：重点覆盖 ns2.0 专属的 BouncyCastle 验签后端
    /// （Es256Verifier #if !NET 分支——SHA256.Create / BigInteger 路径只在 ns2.0 编译），
    /// 以及段读写（四字段条目）、checkpoint 验签、proof 解析验证在该目标上的行为一致性。
    /// 运行器为 net10.0，被测 DLL 为 ns2.0 构建。
    /// </summary>
    public class LogNetStandardSmokeTests : IDisposable
    {
        private readonly string _dir;

        public LogNetStandardSmokeTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "aigc-log-ns20-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
            GC.SuppressFinalize(this);
        }

        private static readonly DateTimeOffset Ts = new(2026, 9, 24, 8, 30, 0, TimeSpan.Zero);

        private static (byte[] Spki, byte[] Message, byte[] P1363) SignSample()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] message = Encoding.UTF8.GetBytes("netstandard2.0 es256 verify smoke");
            byte[] p1363 = key.SignData(message, HashAlgorithmName.SHA256);
            return (key.ExportSubjectPublicKeyInfo(), message, p1363);
        }

        [Fact]
        public void Es256Verifier_BouncyCastleBackend_AcceptsAndRejects()
        {
            (byte[] spki, byte[] message, byte[] p1363) = SignSample();
            Assert.True(Es256Verifier.Verify(message, p1363, spki));

            byte[] tampered = (byte[])message.Clone();
            tampered[0] ^= 1;
            Assert.False(Es256Verifier.Verify(tampered, p1363, spki));

            byte[] badSig = (byte[])p1363.Clone();
            badSig[63] ^= 1;
            Assert.False(Es256Verifier.Verify(message, badSig, spki));
            Assert.Throws<ArgumentException>(() => Es256Verifier.Verify(message, new byte[10], spki));
        }

        [Fact]
        public void CheckpointVerifier_Ns20_WorksEndToEnd()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] spki = key.ExportSubjectPublicKeyInfo();
            string kid = Es256Wire.KidFromSpki(spki);
            byte[] point = new byte[65];
            spki.AsSpan(26).CopyTo(point);
            var jwk = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["x"] = Es256Wire.Base64UrlEncode(point.AsSpan(1, 32).ToArray()),
                ["y"] = Es256Wire.Base64UrlEncode(point.AsSpan(33, 32).ToArray()),
            };
            var keys = KeyFile.Of(new KeyRecord(kid, "ES256", jwk, KeyStatus.Active,
                new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null, null));

            var entries = new List<LogEntry>
            {
                new LogEntry(0, Ts, "sha256:" + new string('c', 64), "sha256:" + new string('a', 64)),
                new LogEntry(1, Ts.AddMinutes(1), "sha256:" + new string('d', 64), "sha256:" + new string('b', 64)),
            };
            // BclP256Signer 仅 net 编译；ns2.0 冒烟用等价内联签名器（IReportSigner 契约：64B P1363）
            IReportSigner signer = new InlineSigner(key);
            Checkpoint cp = CheckpointBuilder.BuildCheckpoint(entries, 2, Ts.AddHours(1), kid, null, signer);

            CheckpointVerification ok = CheckpointVerifier.Verify(cp, keys);
            Assert.True(ok.Valid);

            CheckpointVerification bad = CheckpointVerifier.Verify(cp with
            {
                Sha256RootHash = string.Concat("sha256:0", cp.Sha256RootHash.AsSpan(8)),
            }, keys);
            Assert.False(bad.Valid);
        }

        private sealed class InlineSigner : IReportSigner
        {
            private readonly ECDsa _key;
            public InlineSigner(ECDsa key) => _key = key;
            public byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256);
        }

        [Fact]
        public void SegmentRoundtrip_And_ProofCodec_Ns20()
        {
            string segmentDir = Path.Combine(_dir, "segments");
            var entry = new LogEntry(7, Ts, "sha256:" + new string('c', 64), "sha256:" + new string('a', 64));
            using (var writer = new SegmentWriter(new SegmentWriterOptions { Directory = segmentDir }))
            {
                writer.Append(entry);
            }
            var read = SegmentReader.Read(Path.Combine(segmentDir, "000000000007.jsonl"));
            Assert.Empty(read.Diagnostics);
            var roundtripped = Assert.Single(read.Entries);
            Assert.Equal(entry, roundtripped);

            InclusionProof proof = ProofFactory.Generate(new[] { entry }, 7);
            Assert.Empty(proof.AuditPath); // 单叶树路径为空
            Assert.True(ProofChecker.Verify(entry, ProofCodec.Parse(ProofCodec.Serialize(proof)),
                7, CheckpointBuilder.RootHashFromEntries(new[] { entry })));
        }
    }
}
