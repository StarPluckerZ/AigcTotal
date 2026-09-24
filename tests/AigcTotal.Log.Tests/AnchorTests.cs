using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AigcTotal.Log.Anchoring;
using AigcTotal.Log.Checkpoints;
using AigcTotal.Log.Keys;
using AigcTotal.Log.Segments;
using AigcTotal.Log.Signing;
using Xunit;

namespace AigcTotal.Log.Tests
{
    /// <summary>锚定归档：确定性 tar（可复现构建）、manifest 整验（篡改检测）、checkpoint 见证查询。</summary>
    public class AnchorTests : IDisposable
    {
        private readonly string _dir;

        public AnchorTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "aigc-anchor-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
            GC.SuppressFinalize(this);
        }

        private string PublicDir => Path.Combine(_dir, "public");

        private string BuildSampleLog(int entryCount)
        {
            Directory.CreateDirectory(Path.Combine(PublicDir, "segments"));
            Directory.CreateDirectory(Path.Combine(PublicDir, "checkpoints"));
            Directory.CreateDirectory(Path.Combine(PublicDir, "well-known"));

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var signer = new BclP256Signer(key);
            byte[] spki = key.ExportSubjectPublicKeyInfo();
            string kid = Es256Wire.KidFromSpki(spki);
            byte[] point = spki.AsSpan(26).ToArray();
            var jwk = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["x"] = Es256Wire.Base64UrlEncode(point.AsSpan(1, 32)),
                ["y"] = Es256Wire.Base64UrlEncode(point.AsSpan(33, 32)),
            };
            var record = new KeyRecord(kid, "ES256", jwk, KeyStatus.Active,
                new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null, null);

            using (var writer = new SegmentWriter(new SegmentWriterOptions { Directory = Path.Combine(PublicDir, "segments") }))
            {
                for (int i = 0; i < entryCount; i++)
                {
                    writer.Append(new LogEntry(i,
                        new DateTimeOffset(2026, 9, 24, 8, i, 0, TimeSpan.Zero),
                        "sha256:" + new string('a', 64), "sha256:" + new string('b', 64)));
                }
            }
            var log = PublicLogDirectory.Load(Path.Combine(PublicDir, "segments"));
            Checkpoint cp = CheckpointBuilder.BuildCheckpoint(log.Entries, entryCount,
                new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero), kid, null, signer);
            File.WriteAllText(Path.Combine(PublicDir, "checkpoints", "000000000001.json"),
                CheckpointCodec.ToCanonicalJson(cp) + "\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(PublicDir, "well-known", "keys.json"),
                KeyStore.Serialize(KeyFile.Of(record)), new UTF8Encoding(false));
            return kid;
        }

        [Fact]
        public void Publish_IsDeterministic_AndGnuTarCompatible()
        {
            BuildSampleLog(3);
            var provider = new LocalAnchorProvider(Path.Combine(_dir, "anchors"));

            AnchorPublication first = provider.Publish(PublicDir, "2026-09-24", Path.Combine(_dir, "anchors"));
            AnchorPublication second = provider.Publish(PublicDir, "2026-09-24", Path.Combine(_dir, "anchors2"));

            Assert.Equal(first.TarballSha256, second.TarballSha256); // 同输入 → 逐字节一致
            Assert.Equal(3, first.FileCount); // 段 1 + checkpoint 1 + keys 1（manifest 不计）
        }

        [Fact]
        public void ReadArchive_Verifies_And_Query_FindsCheckpoint()
        {
            BuildSampleLog(3);
            var provider = new LocalAnchorProvider(Path.Combine(_dir, "anchors"));
            provider.Publish(PublicDir, "2026-09-24", Path.Combine(_dir, "anchors"));

            var log = PublicLogDirectory.Load(Path.Combine(PublicDir, "segments"), Path.Combine(PublicDir, "checkpoints"));
            string fingerprint = CheckpointBuilder.FingerprintOf(log.Checkpoints[0]);

            // 目录源与单文件源均可查询
            AnchorWitness? fromDir = provider.Query(fingerprint);
            AnchorWitness? fromFile = new LocalAnchorProvider(
                Path.Combine(_dir, "anchors", "2026-09-24.tar")).Query(fingerprint);

            Assert.NotNull(fromDir);
            Assert.Equal("2026-09-24", fromDir!.Date);
            Assert.Equal(fingerprint, fromDir.CheckpointFingerprint);
            Assert.NotNull(fromFile);

            Assert.Null(provider.Query("sha256:" + new string('0', 64))); // 不存在的指纹
        }

        [Fact]
        public void ReadArchive_TamperedFile_Rejected()
        {
            BuildSampleLog(3);
            var provider = new LocalAnchorProvider(Path.Combine(_dir, "anchors"));
            AnchorPublication publication = provider.Publish(PublicDir, "2026-09-24", Path.Combine(_dir, "anchors"));

            // 篡改 tar 内一个字节（段文件区内）→ manifest SHA-256 失配
            byte[] tar = File.ReadAllBytes(publication.TarballPath);
            tar[tar.Length / 2] ^= 0x01;
            string tampered = Path.Combine(_dir, "tampered.tar");
            File.WriteAllBytes(tampered, tar);

            Assert.Throws<FormatException>(() => LocalAnchorProvider.ReadArchive(tampered));
        }

        [Fact]
        public void ReadArchive_Truncated_Rejected()
        {
            BuildSampleLog(3);
            var provider = new LocalAnchorProvider(Path.Combine(_dir, "anchors"));
            AnchorPublication publication = provider.Publish(PublicDir, "2026-09-24", Path.Combine(_dir, "anchors"));

            byte[] tar = File.ReadAllBytes(publication.TarballPath);
            string truncated = Path.Combine(_dir, "truncated.tar");
            File.WriteAllBytes(truncated, tar.Take(tar.Length - 600).ToArray());

            Assert.Throws<FormatException>(() => LocalAnchorProvider.ReadArchive(truncated));
        }

        [Fact]
        public void DeterministicTar_Roundtrip_And_RejectsBadInput()
        {
            var entries = new List<(string, byte[])>
            {
                ("b/second.txt", Encoding.UTF8.GetBytes("second")),
                ("a/first.bin", new byte[] { 0, 1, 2, 3 }),
                ("empty.txt", Array.Empty<byte>()),
            };
            string path = Path.Combine(_dir, "roundtrip.tar");
            DeterministicTar.Write(path, entries);

            List<(string Path, byte[] Content)> read = DeterministicTar.Read(path);
            Assert.Equal(3, read.Count);
            Assert.Equal("a/first.bin", read[0].Path); // Ordinal 排序
            Assert.Equal(new byte[] { 0, 1, 2, 3 }, read[0].Content);
            Assert.Equal("b/second.txt", read[1].Path);
            Assert.Equal(Encoding.UTF8.GetBytes("second"), read[1].Content);
            Assert.Equal("empty.txt", read[2].Path);
            Assert.Empty(read[2].Content);

            Assert.Throws<ArgumentException>(() => DeterministicTar.Write(path, new[] { ("/abs/path", new byte[1]) }));
            Assert.Throws<ArgumentException>(() => DeterministicTar.Write(path, new[] { (new string('x', 120), new byte[1]) }));
        }
    }
}
