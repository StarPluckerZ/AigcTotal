using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AigcTotal.Log;
using AigcTotal.Log.Anchoring;
using AigcTotal.Log.Checkpoints;
using AigcTotal.Log.Keys;
using AigcTotal.Log.Proofs;
using AigcTotal.Log.Segments;
using AigcTotal.Log.Signing;
using AigcTotal.Report;

namespace AigcTotal.LogHost
{
    /// <summary>
    /// aigc-log-host：编排器的本地宿主替身（D3——本阶段不承担上线职责，只打通链路）：
    ///   init        建公开物目录骨架 + 生成签发密钥（公钥进 well-known/keys.json，私钥另存运营侧）
    ///   sign        对报告信封签名 → SignedReport（外层 {"report":…,"signatures":[…]}）
    ///   append      解析签名报告 → 追加日志条目（input_sha256 + report_sha256；每调用新起一段）
    ///   checkpoint  全量建树 → 签发 checkpoint（链接上一枚）→ 为每条已入日志条目补齐 proof.json
    ///   anchor      把 segments/ + checkpoints/ + well-known/ 打日期 tarball（可复现）落归档目录
    /// 单写者契约：同一公开物目录同时只允许一个宿主进程写入。
    /// </summary>
    public static class LogHost
    {
        public static int Main(string[] args)
        {
            return Run(args, Console.Out, Console.Error);
        }

        public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
        {
            ArgumentNullException.ThrowIfNull(args);
            if (args.Length == 0)
            {
                WriteUsage(stderr);
                return 2;
            }
            try
            {
                switch (args[0])
                {
                    case "--help":
                    case "-h":
                        WriteUsage(stdout);
                        return 0;
                    case "init": return RunInit(args.AsSpan(1), stdout, stderr);
                    case "sign": return RunSign(args.AsSpan(1), stdout, stderr);
                    case "append": return RunAppend(args.AsSpan(1), stdout, stderr);
                    case "checkpoint": return RunCheckpoint(args.AsSpan(1), stdout, stderr);
                    case "anchor": return RunAnchor(args.AsSpan(1), stdout, stderr);
                    case "keys": return RunKeys(args.AsSpan(1), stdout, stderr);
                    default:
                        stderr.WriteLine($"error: unknown command '{args[0]}'");
                        WriteUsage(stderr);
                        return 2;
                }
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                stderr.WriteLine($"error: {ex.Message}");
                return 2;
            }
        }

        private static int RunInit(ReadOnlySpan<string> args, TextWriter stdout, TextWriter stderr)
        {
            string? dir = null, keyOut = null, created = null;
            ParseOptions(args, stderr, ("--dir", v => dir = v), ("--key-out", v => keyOut = v), ("--created", v => created = v));
            if (dir == null) { stderr.WriteLine("error: init requires --dir <publicDir>"); return 2; }

            foreach (string sub in new[] { "segments", "checkpoints", "proofs", "well-known", })
            {
                Directory.CreateDirectory(Path.Combine(dir, sub));
            }
            keyOut ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dir))!, "aigc-signing-key.pkcs8.pem");
            if (File.Exists(Path.Combine(dir, "well-known", "keys.json")))
            {
                stderr.WriteLine("error: keys.json already exists (use aigc-keys tooling to rotate)");
                return 2;
            }

            DateTimeOffset createdUtc = created != null ? ParseTimestamp(created) : DateTimeOffset.UtcNow;
            GeneratedKey generated = KeyStore.Generate(createdUtc);
            File.WriteAllText(Path.Combine(dir, "well-known", "keys.json"),
                KeyStore.Serialize(KeyFile.Of(generated.Record)) + "\n",
                new UTF8Encoding(false));
            WritePem(keyOut!, "PRIVATE KEY", generated.PrivatePkcs8);

            stdout.WriteLine($"keys: kid={generated.Record.Kid}");
            stdout.WriteLine($"public: {Path.Combine(dir, "well-known", "keys.json")}");
            stdout.WriteLine($"private: {keyOut} (operator-side; never publish)");
            return 0;
        }

        private static int RunSign(ReadOnlySpan<string> args, TextWriter stdout, TextWriter stderr)
        {
            string? report = null, keyPath = null, output = null;
            ParseOptions(args, stderr, ("--report", v => report = v), ("--key", v => keyPath = v), ("--out", v => output = v));
            if (report == null) { stderr.WriteLine("error: sign requires --report <report.json>"); return 2; }
            if (keyPath == null) { stderr.WriteLine("error: sign requires --key <pkcs8.pem>"); return 2; }
            output ??= report + ".signed.json";

            Dictionary<string, object?> envelope;
            try
            {
                envelope = CanonicalJson.Deserialize(File.ReadAllText(report));
            }
            catch (FormatException ex)
            {
                stderr.WriteLine($"error: report is not canonical-JSON parseable: {ex.Message}");
                return 2;
            }

            using ECDsa key = LoadPrivateKey(keyPath);
            string kid = KidOf(key);
            var signed = SignedReportCodec.Sign(envelope, kid, new BclP256Signer(key));
            File.WriteAllText(output!, signed.CanonicalJson + "\n", new UTF8Encoding(false));
            stdout.WriteLine($"signed: {output} (kid={kid}, report={signed.ReportSha256()})");
            return 0;
        }

        private static int RunAppend(ReadOnlySpan<string> args, TextWriter stdout, TextWriter stderr)
        {
            string? dir = null, report = null, timestamp = null;
            ParseOptions(args, stderr, ("--dir", v => dir = v), ("--report", v => report = v), ("--timestamp", v => timestamp = v));
            if (dir == null) { stderr.WriteLine("error: append requires --dir <publicDir>"); return 2; }
            if (report == null) { stderr.WriteLine("error: append requires --report <signed-report.json>"); return 2; }

            SignedReport signed = SignedReportCodec.Parse(File.ReadAllText(report));
            string inputSha = ReadInputSha(signed);
            string reportSha = signed.ReportSha256();

            // 序号恢复（D4-2）：从段文件重放重建（读侧零数据库）
            var log = PublicLogDirectory.Load(Path.Combine(dir, "segments"));
            long nextSeq = log.Entries.Count > 0 ? log.Entries[log.Entries.Count - 1].Sequence + 1 : 0;
            DateTimeOffset ts = timestamp != null ? ParseTimestamp(timestamp) : DateTimeOffset.UtcNow;

            using (var writer = new SegmentWriter(new SegmentWriterOptions { Directory = Path.Combine(dir, "segments") }))
            {
                writer.Append(new LogEntry(nextSeq, ts, inputSha, reportSha));
            }
            stdout.WriteLine($"appended: seq={nextSeq} input={inputSha} report={reportSha}");
            return 0;
        }

        private static int RunCheckpoint(ReadOnlySpan<string> args, TextWriter stdout, TextWriter stderr)
        {
            string? dir = null, keyPath = null, timestamp = null;
            ParseOptions(args, stderr, ("--dir", v => dir = v), ("--key", v => keyPath = v), ("--timestamp", v => timestamp = v));
            if (dir == null) { stderr.WriteLine("error: checkpoint requires --dir <publicDir>"); return 2; }
            if (keyPath == null) { stderr.WriteLine("error: checkpoint requires --key <pkcs8.pem>"); return 2; }

            var log = PublicLogDirectory.Load(Path.Combine(dir, "segments"), Path.Combine(dir, "checkpoints"));
            if (log.Entries.Count == 0) { stderr.WriteLine("error: no entries to checkpoint"); return 2; }
            string? prev = log.Checkpoints.Count > 0
                ? CheckpointBuilder.FingerprintOf(log.Checkpoints[log.Checkpoints.Count - 1])
                : null;
            // 链语义：tree_size 与时间严格递增
            DateTimeOffset ts = timestamp != null
                ? ParseTimestamp(timestamp)
                : (log.Checkpoints.Count > 0
                    ? log.Checkpoints[log.Checkpoints.Count - 1].TimestampUtc.AddSeconds(1)
                    : DateTimeOffset.UtcNow);

            using ECDsa key = LoadPrivateKey(keyPath);
            string kid = KidOf(key);
            Checkpoint checkpoint = CheckpointBuilder.BuildCheckpoint(
                log.Entries, treeSize: log.Entries.Count, timestampUtc: ts, kid: kid,
                prevCheckpointHash: prev, signer: new BclP256Signer(key));
            string checkpointPath = Path.Combine(dir, "checkpoints",
                checkpoint.TreeSize.ToString("D12", System.Globalization.CultureInfo.InvariantCulture) + ".json");
            File.WriteAllText(checkpointPath, CheckpointCodec.ToCanonicalJson(checkpoint) + "\n", new UTF8Encoding(false));

            // proof 补齐（W3-④：编排器在 checkpoint 时点为每条条目生成）
            for (int i = 0; i < log.Entries.Count; i++)
            {
                InclusionProof proof = ProofFactory.Generate(log.Entries, log.Entries[i].Sequence);
                string proofPath = Path.Combine(dir, "proofs",
                    log.Entries[i].Sequence.ToString("D12", System.Globalization.CultureInfo.InvariantCulture) + ".proof.json");
                File.WriteAllText(proofPath, ProofCodec.Serialize(proof) + "\n", new UTF8Encoding(false));
            }
            stdout.WriteLine($"checkpoint: tree_size={checkpoint.TreeSize} root={checkpoint.Sha256RootHash} kid={kid}");
            stdout.WriteLine($"proofs: {log.Entries.Count} written under proofs/");
            return 0;
        }

        private static int RunAnchor(ReadOnlySpan<string> args, TextWriter stdout, TextWriter stderr)
        {
            string? dir = null, date = null, output = null;
            ParseOptions(args, stderr, ("--dir", v => dir = v), ("--date", v => date = v), ("--out", v => output = v));
            if (dir == null) { stderr.WriteLine("error: anchor requires --dir <publicDir>"); return 2; }
            if (date == null) { stderr.WriteLine("error: anchor requires --date <yyyy-MM-dd>"); return 2; }
            output ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dir))!, "anchors");

            // proofs/ 是便利物不进锚（可由段重建）；锚只见证 segments/ + checkpoints/ + well-known/
            var provider = new LocalAnchorProvider(output);
            AnchorPublication publication = provider.Publish(dir, date, output);
            stdout.WriteLine($"anchored: {publication.TarballPath} ({publication.FileCount} files, sha256={publication.TarballSha256})");
            return 0;
        }

        // —— keys 工具（W3-⑤）：生成/轮换/退役/吊销 ——
        // 发布流程：well-known/keys.json 随开源仓提交（git 历史即锚定）；私钥只在运营侧。

        private static int RunKeys(ReadOnlySpan<string> args, TextWriter stdout, TextWriter stderr)
        {
            if (args.Length == 0)
            {
                stderr.WriteLine("usage: aigc-log-host keys generate|retire|revoke ...");
                return 2;
            }
            switch (args[0])
            {
                case "generate": return KeysGenerate(args.Slice(1), stdout, stderr);
                case "retire": return KeysRetire(args.Slice(1), "retired", stdout, stderr);
                case "revoke": return KeysRetire(args.Slice(1), "revoked", stdout, stderr);
                default:
                    stderr.WriteLine($"error: unknown keys subcommand '{args[0]}'");
                    return 2;
            }
        }

        private static int KeysGenerate(ReadOnlySpan<string> args, TextWriter stdout, TextWriter stderr)
        {
            string? dir = null, keyOut = null, created = null;
            ParseOptions(args, stderr, ("--dir", v => dir = v), ("--key-out", v => keyOut = v), ("--created", v => created = v));
            if (dir == null) { stderr.WriteLine("error: keys generate requires --dir <publicDir>"); return 2; }

            string keysPath = Path.Combine(dir, "well-known", "keys.json");
            DateTimeOffset createdUtc = created != null ? ParseTimestamp(created) : DateTimeOffset.UtcNow;
            GeneratedKey generated = KeyStore.Generate(createdUtc);

            var records = new List<KeyRecord>();
            if (File.Exists(keysPath))
            {
                records.AddRange(KeyStore.Load(keysPath).Keys); // 轮换：旧钥保留（状态由 retire/revoke 变更）
            }
            records.Add(generated.Record);
            Directory.CreateDirectory(Path.GetDirectoryName(keysPath)!);
            File.WriteAllText(keysPath, KeyStore.Serialize(KeyFile.Of(records.ToArray())) + "\n", new UTF8Encoding(false));
            keyOut ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dir))!, generated.Record.Kid + ".pkcs8.pem");
            WritePem(keyOut!, "PRIVATE KEY", generated.PrivatePkcs8);

            stdout.WriteLine($"generated: kid={generated.Record.Kid} (now {records.Count} key(s) in keys.json)");
            stdout.WriteLine($"public: {keysPath}");
            stdout.WriteLine($"private: {keyOut} (operator-side; never publish)");
            return 0;
        }

        private static int KeysRetire(ReadOnlySpan<string> args, string action, TextWriter stdout, TextWriter stderr)
        {
            string? dir = null, kid = null, timestamp = null, reason = null;
            ParseOptions(args, stderr, ("--dir", v => dir = v), ("--kid", v => kid = v),
                ("--timestamp", v => timestamp = v), ("--reason", v => reason = v));
            if (dir == null) { stderr.WriteLine($"error: keys {action} requires --dir <publicDir>"); return 2; }
            if (kid == null) { stderr.WriteLine($"error: keys {action} requires --kid <kid>"); return 2; }
            DateTimeOffset ts = timestamp != null ? ParseTimestamp(timestamp) : DateTimeOffset.UtcNow;

            string keysPath = Path.Combine(dir, "well-known", "keys.json");
            if (!File.Exists(keysPath)) { stderr.WriteLine($"error: {keysPath} not found"); return 2; }
            var keys = KeyStore.Load(keysPath);
            KeyRecord? target = keys.Find(kid);
            if (target == null) { stderr.WriteLine($"error: kid '{kid}' not found"); return 2; }
            if (target.Status == KeyStatus.Revoked) { stderr.WriteLine("error: key already revoked"); return 2; }

            var records = new List<KeyRecord>();
            foreach (KeyRecord record in keys.Keys)
            {
                records.Add(record.Kid == kid
                    ? action == "retired"
                        ? record with { Status = KeyStatus.VerifyOnly, Retired = ts }
                        : record with { Status = KeyStatus.Revoked, Revoked = ts,
                            RevokedReason = reason ?? (record.RevokedReason ?? "operator decision") }
                    : record);
            }
            File.WriteAllText(keysPath, KeyStore.Serialize(KeyFile.Of(records.ToArray())) + "\n", new UTF8Encoding(false));
            stdout.WriteLine($"{action}: kid={kid} at {LogTime.Format(ts)}");
            return 0;
        }

        // —— 公共 ——

        private static void ParseOptions(ReadOnlySpan<string> args, TextWriter stderr,
            params (string Name, Action<string> Set)[] options)
        {
            for (int i = 0; i < args.Length; i++)
            {
                bool matched = false;
                foreach (var (name, set) in options)
                {
                    if (args[i] == name)
                    {
                        if (i + 1 >= args.Length)
                        {
                            throw new ArgumentException($"{name} requires a value");
                        }
                        set(args[++i]);
                        matched = true;
                        break;
                    }
                }
                if (!matched) throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }

        private static string ReadInputSha(SignedReport signed)
        {
            if (signed.Envelope.TryGetValue("input", out object? inputObj)
                && inputObj is Dictionary<string, object?> input
                && input.TryGetValue("sha256", out object? shaObj) && shaObj is string sha)
            {
                return sha;
            }
            throw new FormatException("report envelope has no input.sha256 — cannot log entry");
        }

        private static ECDsa LoadPrivateKey(string keyPath)
        {
            string pem = File.ReadAllText(keyPath);
            string base64 = pem
                .Replace("-----BEGIN PRIVATE KEY-----", "")
                .Replace("-----END PRIVATE KEY-----", "")
                .Replace("\r", "").Replace("\n", "");
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(base64), out _);
            if (key.KeySize != 256) throw new ArgumentException("signing key must be P-256", keyPath);
            return key;
        }

        private static string KidOf(ECDsa key)
        {
            return Es256Wire.KidFromSpki(key.ExportSubjectPublicKeyInfo());
        }

        private static DateTimeOffset ParseTimestamp(string text)
        {
            if (!LogTime.TryParse(text, out DateTimeOffset value))
            {
                throw new FormatException($"timestamp must be RFC 3339 seconds precision (…Z): {text}");
            }
            return value;
        }

        private static void WritePem(string path, string label, byte[] der)
        {
            var sb = new StringBuilder();
            sb.Append("-----BEGIN ").Append(label).Append("-----\n");
            string base64 = Convert.ToBase64String(der);
            for (int i = 0; i < base64.Length; i += 64)
            {
                sb.Append(base64, i, Math.Min(64, base64.Length - i)).Append('\n');
            }
            sb.Append("-----END ").Append(label).Append("-----\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static void WriteUsage(TextWriter writer)
        {
            writer.WriteLine("aigc-log-host — local orchestrator stand-in (development/demo, single writer)");
            writer.WriteLine();
            writer.WriteLine("usage:");
            writer.WriteLine("  aigc-log-host init --dir <publicDir> [--key-out <pkcs8.pem>] [--created <ts>]");
            writer.WriteLine("  aigc-log-host sign --report <report.json> --key <pkcs8.pem> [--out <signed.json>]");
            writer.WriteLine("  aigc-log-host append --dir <publicDir> --report <signed.json> [--timestamp <ts>]");
            writer.WriteLine("  aigc-log-host checkpoint --dir <publicDir> --key <pkcs8.pem> [--timestamp <ts>]");
            writer.WriteLine("  aigc-log-host anchor --dir <publicDir> --date <yyyy-MM-dd> [--out <archiveDir>]");
            writer.WriteLine("  aigc-log-host keys generate --dir <publicDir> [--key-out <pem>] [--created <ts>]");
            writer.WriteLine("  aigc-log-host keys retire --dir <publicDir> --kid <kid> [--timestamp <ts>]");
            writer.WriteLine("  aigc-log-host keys revoke --dir <publicDir> --kid <kid> [--reason <text>] [--timestamp <ts>]");
            writer.WriteLine();
            writer.WriteLine("public dir layout: segments/ checkpoints/ proofs/ well-known/keys.json");
            writer.WriteLine("timestamps: RFC 3339 UTC seconds precision, e.g. 2026-09-24T08:00:00Z");
            writer.WriteLine("each 'append' invocation starts a fresh segment (crash-safe CreateNew; cross-restart");
            writer.WriteLine("segment resume is deferred to the Postgres orchestrator phase).");
        }
    }
}
