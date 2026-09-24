using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AigcTotal.GB45438;
using AigcTotal.Report;
using Xunit;
using Xunit.Abstractions;

namespace AigcTotal.Verify.Tests
{
    /// <summary>
    /// W6 端到端验收（对应《开发计划》第 2 步验收，去云化）：
    /// 本地宿主签发报告 → 仅凭公开物（segments/ + checkpoints/ + keys.json + 每日 tarball）
    /// 由 aigc-verify 独立完成验证；篡改报告/段文件/checkpoint/proof 任一字节，验证失败。
    /// </summary>
    public class EndToEndTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _dir;

        public EndToEndTests(ITestOutputHelper output)
        {
            _output = output;
            _dir = Path.Combine(Path.GetTempPath(), "aigc-e2e-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
            GC.SuppressFinalize(this);
        }

        private string Public => Path.Combine(_dir, "public");
        private string Anchors => Path.Combine(_dir, "anchors");
        private string KeyPath => Path.Combine(_dir, "key.pem");

        private static string Corpus(string name) =>
            Path.Combine(AppContext.BaseDirectory, "corpus", name);

        private int Host(params string[] args) => Run(AigcTotal.LogHost.LogHost.Run, args);

        private int Verify(params string[] args) => Run(AigcTotal.Verify.Cli.VerifyCli.Run, args);

        private int Run(Func<string[], TextWriter, TextWriter, int> program, string[] args)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = program(args, stdout, stderr);
            _output.WriteLine("$ " + string.Join(' ', args));
            _output.WriteLine(stdout.ToString().TrimEnd());
            if (stderr.ToString().Length > 0) _output.WriteLine("stderr: " + stderr.ToString().TrimEnd());
            return code;
        }

        /// <summary>在文件内 needle 首次出现后翻转一个 hex 字符（确定性单字节篡改）。</summary>
        private static void FlipAfter(string path, string needle)
        {
            string text = File.ReadAllText(path);
            int at = text.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(at >= 0, $"needle '{needle}' not found in {path}");
            char c = text[at + needle.Length];
            char flipped = c == 'a' ? 'b' : (c == '0' ? '1' : '0');
            File.WriteAllText(path, string.Concat(text.AsSpan(0, at + needle.Length),
                flipped.ToString(), text.AsSpan(at + needle.Length + 1)), new UTF8Encoding(false));
        }

        /// <summary>构造一份签名报告（真实核查 corpus 文件 → 信封 → 宿主签名）。</summary>
        private string IssueSignedReport(string corpusName, string timestamp)
        {
            byte[] input = File.ReadAllBytes(Corpus(corpusName));
            string inputHash = "sha256:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant();
            VerificationResult result = AigcLabelVerifier.Verify(input);
            ReportEnvelope envelope = AigcReportBuilder.Build(
                result, inputHash, input.Length, "e2e-host", "0.1.0",
                reportId: null,
                timestampUtc: DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal));

            string raw = Path.Combine(_dir, corpusName + ".report.json");
            File.WriteAllText(raw, envelope.CanonicalJson + "\n", new UTF8Encoding(false));
            string signed = raw + ".signed.json";
            Assert.Equal(0, Host("sign", "--report", raw, "--key", KeyPath, "--out", signed));
            return signed;
        }

        /// <summary>标准链路：init → 签发两份报告 → append → checkpoint → anchor。</summary>
        private void BootstrapLog()
        {
            Assert.Equal(0, Host("init", "--dir", Public, "--key-out", KeyPath, "--created", "2020-01-01T00:00:00Z"));

            string first = IssueSignedReport("png-label-valid.png", "2026-09-24T09:00:00Z");
            string second = IssueSignedReport("mp4-udta-aigc.mp4", "2026-09-24T09:05:00Z");
            Assert.Equal(0, Host("append", "--dir", Public, "--report", first, "--timestamp", "2026-09-24T09:10:00Z"));
            Assert.Equal(0, Host("append", "--dir", Public, "--report", second, "--timestamp", "2026-09-24T09:11:00Z"));
            Assert.Equal(0, Host("checkpoint", "--dir", Public, "--key", KeyPath, "--timestamp", "2026-09-24T10:00:00Z"));
            Assert.Equal(0, Host("anchor", "--dir", Public, "--date", "2026-09-24", "--out", Anchors));
        }

        private string Signed(string corpusName) =>
            Path.Combine(_dir, corpusName + ".report.json.signed.json");

        private string[] VerifyArgs(string report, string? proof = null)
        {
            var args = new List<string>
            {
                "verify", report,
                "--keys", Path.Combine(Public, "well-known", "keys.json"),
                "--segments", Path.Combine(Public, "segments"),
                "--checkpoints", Path.Combine(Public, "checkpoints"),
                "--anchor", Path.Combine(Anchors, "2026-09-24.tar"),
            };
            if (proof != null) args.AddRange(new[] { "--proof", proof });
            return args.ToArray();
        }

        private string[] AuditArgs(string? anchor = null)
        {
            var args = new List<string>
            {
                "audit",
                "--segments", Path.Combine(Public, "segments"),
                "--checkpoints", Path.Combine(Public, "checkpoints"),
                "--keys", Path.Combine(Public, "well-known", "keys.json"),
            };
            if (anchor != null) args.AddRange(new[] { "--anchor", anchor });
            return args.ToArray();
        }

        [Fact]
        public void FullChain_ThirdPartyVerifies_WithAndWithoutProof()
        {
            BootstrapLog();
            string proof = Path.Combine(Public, "proofs", "000000000000.proof.json");

            Assert.Equal(0, Verify(VerifyArgs(Signed("png-label-valid.png"), proof)));
            Assert.Equal(0, Verify(VerifyArgs(Signed("png-label-valid.png")))); // 无 proof：自行重建路径
            Assert.Equal(0, Verify(VerifyArgs(Signed("mp4-udta-aigc.mp4"), Path.Combine(Public, "proofs", "000000000001.proof.json"))));
        }

        [Fact]
        public void Lookup_ByInputSha_FindsEntry()
        {
            BootstrapLog();

            string inputHash = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(Corpus("png-label-valid.png")))).ToLowerInvariant();
            Assert.Equal(0, Verify("lookup", "--input-sha256", inputHash,
                "--segments", Path.Combine(Public, "segments")));

            Assert.Equal(1, Verify("lookup", "--input-sha256", "sha256:" + new string('0', 64),
                "--segments", Path.Combine(Public, "segments")));
            Assert.Equal(10, Verify("lookup", "--input-sha256", "not-a-hash",
                "--segments", Path.Combine(Public, "segments")));
        }

        [Fact]
        public void Audit_FullRebuild_Passes()
        {
            BootstrapLog();

            Assert.Equal(0, Verify(AuditArgs(Path.Combine(Anchors, "2026-09-24.tar"))));
        }

        [Fact]
        public void Tamper_AnyTrustArtifact_Rejected()
        {
            BootstrapLog();
            string signed = Signed("png-label-valid.png");
            string proof = Path.Combine(Public, "proofs", "000000000000.proof.json");

            // 1) 篡改报告本体一字节（verdict）→ 签名失配
            string badReport = Path.Combine(_dir, "bad-report.json");
            File.WriteAllText(badReport, File.ReadAllText(signed).Replace("\"compliant\"", "\"complianX\""), new UTF8Encoding(false));
            Assert.Equal(1, Verify(VerifyArgs(badReport, proof)));

            // 2) 篡改段文件一字节（首条时间戳 09:10:00 → 09:10:01）→ 重建根失配
            string segment = Path.Combine(Public, "segments", "000000000000.jsonl");
            string segmentBackup = segment + ".bak";
            File.Copy(segment, segmentBackup);
            FlipAfter(segment, "\"timestamp\":\"2026-09-24T09:10:");
            Assert.Equal(1, Verify(VerifyArgs(signed, proof)));
            Assert.Equal(1, Verify(AuditArgs()));
            File.Copy(segmentBackup, segment, overwrite: true);

            // 3) 篡改 checkpoint 根哈希 → 验签失配 + 根对照失配
            string checkpoint = Path.Combine(Public, "checkpoints", "000000000002.json");
            string checkpointBackup = checkpoint + ".bak";
            File.Copy(checkpoint, checkpointBackup);
            FlipAfter(checkpoint, "\"sha256_root_hash\":\"sha256:");
            Assert.Equal(1, Verify(VerifyArgs(signed, proof)));
            File.Copy(checkpointBackup, checkpoint, overwrite: true);

            // 4) 篡改 proof 路径元素首字符 → 包含证明失败
            string badProof = Path.Combine(_dir, "bad-proof.json");
            string proofJson = File.ReadAllText(proof);
            string element = proofJson.Split('"')[3]; // {"audit_path":["<elem>",…
            Assert.True(element.Length > 2);
            string flipped = (element[0] == 'A' ? 'B' : 'A') + element.Substring(1);
            File.WriteAllText(badProof, proofJson.Replace(element, flipped), new UTF8Encoding(false));
            Assert.Equal(1, Verify(VerifyArgs(signed, badProof)));

            // 5) 篡改锚 tarball 一字节 → manifest 整验失败
            string tar = Path.Combine(Anchors, "2026-09-24.tar");
            byte[] tarBytes = File.ReadAllBytes(tar);
            tarBytes[tarBytes.Length / 2] ^= 0x01;
            string badTar = Path.Combine(_dir, "bad.tar");
            File.WriteAllBytes(badTar, tarBytes);
            Assert.Equal(1, Verify(AuditArgs(badTar)));

            // 对照：恢复后整链通过
            Assert.Equal(0, Verify(VerifyArgs(signed, proof)));
        }

        [Fact]
        public void TruncatedChain_RequiresExternalAnchor()
        {
            BootstrapLog();
            string signed = Signed("png-label-valid.png");

            // 追加第三条目 + 新 checkpoint（链长 2），然后删除链首（模拟验证者只拿到尾部快照）
            string third = IssueSignedReport("wav-aigc.wav", "2026-09-24T11:00:00Z");
            Assert.Equal(0, Host("append", "--dir", Public, "--report", third, "--timestamp", "2026-09-24T11:05:00Z"));
            Assert.Equal(0, Host("checkpoint", "--dir", Public, "--key", KeyPath, "--timestamp", "2026-09-24T12:00:00Z"));
            File.Delete(Path.Combine(Public, "checkpoints", "000000000002.json"));

            string proof = Path.Combine(Public, "proofs", "000000000000.proof.json");

            // 有锚（锚归档内的 checkpoint #2 见证链首）→ 通过
            Assert.Equal(0, Verify(VerifyArgs(signed, proof)));

            // 无锚 → 截断链必须失败（信任终点缺失）
            var argList = new List<string>(VerifyArgs(signed, proof));
            int anchorIdx = argList.IndexOf("--anchor");
            argList.RemoveRange(anchorIdx, 2);
            Assert.Equal(1, Verify(argList.ToArray()));
        }

        [Fact]
        public void Verify_UnknownReport_NotLogged_Rejected()
        {
            BootstrapLog();
            // 签发但从未入日志的报告
            string ghost = IssueSignedReport("article-plain.txt", "2026-09-24T13:00:00Z");
            Assert.Equal(1, Verify(VerifyArgs(ghost)));
        }
    }
}
