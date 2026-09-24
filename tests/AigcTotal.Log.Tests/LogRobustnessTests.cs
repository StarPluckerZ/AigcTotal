using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AigcTotal.Log.Anchoring;
using AigcTotal.Log.Checkpoints;
using AigcTotal.Log.Keys;
using AigcTotal.Log.Proofs;
using AigcTotal.Log.Segments;
using AigcTotal.Log.Signing;
using Xunit;

namespace AigcTotal.Log.Tests
{
    /// <summary>
    /// 透明日志解析面的不可信输入套件（与 tools/AigcTotal.Fuzz 各目标同契约的跨平台轻量防线）：
    /// 篡改/截断/变异 valid 语料后，解析面只允许 FormatException/ArgumentException 逃逸
    /// （文档化的格式拒绝）；SegmentReader.Read 与 DeterministicTar.Read/ReadArchive 语义更强——
    /// 任何异常都不允许逃逸（读侧收集诊断/抛格式异常）。契约外的异常（Overflow、越界、OOM）= bug。
    /// </summary>
    public class LogRobustnessTests
    {
        private static readonly DateTimeOffset Ts = new(2026, 9, 24, 8, 30, 0, TimeSpan.Zero);

        private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(
            SHA256.HashData(bytes)).ToLowerInvariant();

        // —— 有效语料（变异基底） ——

        private static string SegmentLine() =>
            new LogEntry(0, Ts, "sha256:" + new string('c', 64), "sha256:" + new string('a', 64))
                .ToCanonicalLine();

        private static byte[] ValidTar()
        {
            var entries = new List<(string, byte[])>
            {
                ("segments/000000000000.jsonl", Encoding.UTF8.GetBytes(SegmentLine() + "\n")),
                ("checkpoints/000000000001.json", Encoding.UTF8.GetBytes(
                    "{\"kid\":\"k" + new string('1', 64) + "\",\"sha256_root_hash\":\"sha256:"
                    + new string('2', 64) + "\",\"timestamp\":\"2026-09-24T09:00:00Z\",\"tree_size\":1}")),
            };
            string path = Path.Combine(Path.GetTempPath(), "aigc-logrob-" + Guid.NewGuid().ToString("N") + ".tar");
            try
            {
                DeterministicTar.Write(path, entries);
                return File.ReadAllBytes(path);
            }
            finally
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }

        [Fact]
        public void SegmentReader_MutationsAndTruncations_NeverThrow()
        {
            string line = SegmentLine();
            string path = Path.Combine(Path.GetTempPath(), "aigc-logrob-" + Guid.NewGuid().ToString("N") + ".jsonl");
            try
            {
                var random = new Random(Seed: 20260924);
                foreach (byte[] mutant in Mutations(Encoding.UTF8.GetBytes(line + "\n"), random, iterations: 300))
                {
                    File.WriteAllBytes(path, mutant);
                    SegmentReader.Read(path); // 任何异常逃出即 bug（读侧容忍 + 诊断）
                }
                // 退化输入
                foreach (byte[] degenerate in new[]
                {
                    Array.Empty<byte>(),
                    new byte[] { 0x00 },
                    Encoding.UTF8.GetBytes("{"),
                    Encoding.UTF8.GetBytes("{\"seq\":"),
                    Encoding.UTF8.GetBytes("{\"seq\":99999999999999999999}"),   // 超长整数
                    Encoding.UTF8.GetBytes("{\"seq\":9223372036854775808}"),    // > long.Max
                    Encoding.UTF8.GetBytes(line[..20]),
                    new byte[] { 0xFF, 0xFE, 0x00, 0x01 },
                })
                {
                    File.WriteAllBytes(path, degenerate);
                    SegmentReader.Read(path);
                }
            }
            finally
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }

        [Fact]
        public void JsonParsers_Mutations_OnlyFormatOrArgumentEscapes()
        {
            string checkpointJson = CheckpointCodec.ToCanonicalJson(new Checkpoint(
                1, Ts, "sha256:" + new string('2', 64),
                "k" + new string('1', 64), null, "c2ln"));

            // (名称, 有效 JSON, 解析器)
            var cases = new List<(string, string, Func<string, object>)>
            {
                ("checkpoint", checkpointJson, json => CheckpointCodec.Parse(json)),
                ("proof", ProofCodec.Serialize(ProofFactory.Generate(
                    new[] { new LogEntry(0, Ts, "sha256:" + new string('c', 64), "sha256:" + new string('a', 64)) }, 0)),
                    json => ProofCodec.Parse(json)),
            };

            var random = new Random(Seed: 20260925);
            foreach ((string name, string valid, Func<string, object> parse) in cases)
            {
                foreach (string mutant in MutateText(valid, random, iterations: 300))
                {
                    try
                    {
                        parse(mutant);
                    }
                    catch (FormatException)
                    {
                    }
                    catch (ArgumentException)
                    {
                    }
                    // 其余异常逃出即测试失败
                }
            }
        }

        [Fact]
        public void SignedReportAndKeys_Mutations_OnlyFormatOrArgumentEscapes()
        {
            // 真实签名报告（含可解析 kid 的 keys.json）作为变异基底
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] spki = key.ExportSubjectPublicKeyInfo();
            string kid = Es256Wire.KidFromSpki(spki);
            byte[] point = new byte[65];
            spki.AsSpan(26).CopyTo(point);
            string keysJson = KeyStore.Serialize(KeyFile.Of(new KeyRecord(
                kid, "ES256",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kty"] = "EC",
                    ["crv"] = "P-256",
                    ["x"] = Es256Wire.Base64UrlEncode(point.AsSpan(1, 32).ToArray()),
                    ["y"] = Es256Wire.Base64UrlEncode(point.AsSpan(33, 32).ToArray()),
                },
                KeyStatus.Active, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null, null)));
            string reportJson = SignedReportCodec.Sign(
                new Dictionary<string, object?>
                {
                    ["version"] = 1L,
                    ["timestamp"] = "2026-09-24T08:30:00Z",
                    ["input"] = new Dictionary<string, object?>
                    {
                        ["sha256"] = "sha256:" + new string('c', 64),
                        ["bytes"] = 10L,
                    },
                    ["verdict"] = "compliant",
                }, kid, new BclP256Signer(key)).CanonicalJson;

            var random = new Random(Seed: 20260926);
            foreach (string mutant in MutateText(reportJson, random, iterations: 300))
            {
                try
                {
                    SignedReportCodec.Parse(mutant);
                }
                catch (FormatException) { }
                catch (ArgumentException) { }
            }
            foreach (string mutant in MutateText(keysJson, random, iterations: 300))
            {
                try
                {
                    KeyStore.Parse(mutant);
                }
                catch (FormatException) { }
                catch (ArgumentException) { }
            }
            // fuzz 实锤 2026-09-24：顶层缺 keys 键 → 索引器 KeyNotFoundException 逃逸（已修，回归）
            Assert.Throws<FormatException>(() => KeyStore.Parse("{}"));
        }

        [Fact]
        public void Tar_Read_MutationsAndBombs_OnlyFormatEscapes()
        {
            byte[] valid = ValidTar();
            var random = new Random(Seed: 20260927);
            string path = Path.Combine(Path.GetTempPath(), "aigc-logrob-" + Guid.NewGuid().ToString("N") + ".tar");
            try
            {
                foreach (byte[] mutant in Mutations(valid, random, iterations: 400))
                {
                    File.WriteAllBytes(path, mutant);
                    try
                    {
                        DeterministicTar.Read(path);
                    }
                    catch (FormatException) { }
                }

                // 定向炸弹：八进制溢出（> long.Max）、八进制负号（fuzz 实锤 2026-09-24：
                // Convert.ToInt64 抛 ArgumentException）、size 声明巨大、长度非 512 倍数、头部截断
                byte[] octalBomb = (byte[])valid.Clone();
                Buffer.BlockCopy(Encoding.ASCII.GetBytes("7777777777777777777777\0 "), 0,
                    octalBomb, 124, 12); // size 字段
                File.WriteAllBytes(path, octalBomb);
                Assert.Throws<FormatException>(() => DeterministicTar.Read(path));

                byte[] octalMinus = (byte[])valid.Clone();
                Buffer.BlockCopy(Encoding.ASCII.GetBytes("-0000000000\0 "), 0,
                    octalMinus, 124, 12);
                File.WriteAllBytes(path, octalMinus);
                Assert.Throws<FormatException>(() => DeterministicTar.Read(path));

                byte[] checksumBomb = (byte[])valid.Clone();
                Buffer.BlockCopy(Encoding.ASCII.GetBytes("7777777777777777777777\0 "), 0,
                    checksumBomb, 148, 12); // 越过 chksum 字段写入（8+4 字节边界）
                File.WriteAllBytes(path, checksumBomb);
                Assert.Throws<FormatException>(() => DeterministicTar.Read(path));

                foreach (byte[] degenerate in new[]
                {
                    Array.Empty<byte>(),
                    new byte[512],
                    new byte[511],
                    valid.Take(valid.Length / 2).ToArray(),
                })
                {
                    File.WriteAllBytes(path, degenerate);
                    try
                    {
                        DeterministicTar.Read(path);
                        // 空归档（2×512 零块）合法：无条目返回
                    }
                    catch (FormatException) { }
                }
            }
            finally
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }

        [Fact]
        public void AnchorReadArchive_Mutations_OnlyFormatEscapes()
        {
            // 整验归档 = tar + manifest + checkpoint 组合面；锚 tarball 是从外部下载的不可信物
            string publicDir = Path.Combine(Path.GetTempPath(), "aigc-logrob-" + Guid.NewGuid().ToString("N"));
            string anchorDir = Path.Combine(publicDir, "anchors");
            try
            {
                Directory.CreateDirectory(Path.Combine(publicDir, "segments"));
                Directory.CreateDirectory(Path.Combine(publicDir, "checkpoints"));
                Directory.CreateDirectory(Path.Combine(publicDir, "well-known"));
                string segment = Path.Combine(publicDir, "segments", "000000000000.jsonl");
                File.WriteAllText(segment, SegmentLine() + "\n", new UTF8Encoding(false));

                var provider = new LocalAnchorProvider(anchorDir);
                AnchorPublication publication = provider.Publish(publicDir, "2026-09-24", anchorDir);

                byte[] valid = File.ReadAllBytes(publication.TarballPath);
                var random = new Random(Seed: 20260928);
                string mutantPath = Path.Combine(anchorDir, "mutant.tar");
                foreach (byte[] mutant in Mutations(valid, random, iterations: 400))
                {
                    File.WriteAllBytes(mutantPath, mutant);
                    try
                    {
                        LocalAnchorProvider.ReadArchive(mutantPath);
                    }
                    catch (FormatException) { }
                }
            }
            finally
            {
                try { Directory.Delete(publicDir, recursive: true); } catch (IOException) { }
            }
        }

        // —— 变异器（与 GB45438 RobustnessTests 同风格：位翻转 / 覆写 / 截断） ——

        private static IEnumerable<byte[]> Mutations(byte[] original, Random random, int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                byte[] mutant = (byte[])original.Clone();
                int count = 1 + random.Next(8);
                for (int m = 0; m < count && mutant.Length > 0; m++)
                {
                    switch (random.Next(3))
                    {
                        case 0:
                            int bitIndex = random.Next(mutant.Length * 8);
                            mutant[bitIndex / 8] ^= (byte)(1 << (bitIndex % 8));
                            break;
                        case 1:
                            byte[] interesting = { 0x00, 0xFF, 0x7F, 0x80, 0x01, (byte)'\'', (byte)'\\' };
                            mutant[random.Next(mutant.Length)] = interesting[random.Next(interesting.Length)];
                            break;
                        case 2:
                            Array.Resize(ref mutant, random.Next(mutant.Length + 1));
                            break;
                    }
                }
                yield return mutant;
            }
        }

        private static IEnumerable<string> MutateText(string valid, Random random, int iterations)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(valid);
            foreach (byte[] mutant in Mutations(bytes, random, iterations))
            {
                yield return Encoding.UTF8.GetString(mutant);
            }
        }
    }
}
