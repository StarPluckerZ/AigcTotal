using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AigcTotal.Log;
using AigcTotal.Log.Anchoring;
using AigcTotal.Log.Checkpoints;
using AigcTotal.Log.Keys;
using AigcTotal.Log.Merkle;
using AigcTotal.Log.Proofs;
using AigcTotal.Log.Segments;
using AigcTotal.Log.Signing;

namespace AigcTotal.Verify.Cli
{
    /// <summary>
    /// aigc-verify：透明日志第三方验证 CLI（开源，信任路径，零数据库）。
    ///   verify  持报告者全链五步：信封 canonical 重算 → 报告验签 → 段重建树根对照 → 包含证明 → checkpoint 链回溯；
    ///   lookup  仅持同文件者：按 input_sha256 定位条目（存在性 + 时间证明）；
    ///   audit   全量重建审计：链首截断检测、逐 checkpoint 链校验、逐前缀树根对照、可选锚归档整验。
    /// 退出码：0 有效 / 1 验证失败 / 10 处理错误（对齐 aigc-report 风格）。
    /// </summary>
    public static class VerifyCli
    {
        private const int ExitValid = 0;
        private const int ExitInvalid = 1;
        private const int ExitError = 10;

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
                return ExitError;
            }

            try
            {
                switch (args[0])
                {
                    case "--help":
                    case "-h":
                        WriteUsage(stdout);
                        return ExitValid;
                    case "--version":
                        stdout.WriteLine(ToolVersion);
                        return ExitValid;
                    case "verify":
                        return RunVerify(args.AsSpan(1), stdout, stderr);
                    case "lookup":
                        return RunLookup(args.AsSpan(1), stdout, stderr);
                    case "audit":
                        return RunAudit(args.AsSpan(1), stdout, stderr);
                    default:
                        stderr.WriteLine($"error: unknown command '{args[0]}'");
                        WriteUsage(stderr);
                        return ExitError;
                }
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                stderr.WriteLine($"error: {ex.Message}");
                return ExitError;
            }
        }

        // —— verify：持报告者全链五步（docs/transparency-log.md §7）——

        private static int RunVerify(ReadOnlySpan<string> args, TextWriter stdout, TextWriter stderr)
        {
            string? reportPath = null, proofPath = null, keysPath = null, segmentsDir = null, checkpointsDir = null, anchor = null;
            bool offline = false, json = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--proof": proofPath = Next(args, ref i, "--proof"); break;
                    case "--keys": keysPath = Next(args, ref i, "--keys"); break;
                    case "--segments": segmentsDir = Next(args, ref i, "--segments"); break;
                    case "--checkpoints": checkpointsDir = Next(args, ref i, "--checkpoints"); break;
                    case "--anchor": anchor = Next(args, ref i, "--anchor"); break;
                    case "--offline": offline = true; break;
                    case "--json": json = true; break;
                    default:
                        if (reportPath == null && !args[i].StartsWith("--", StringComparison.Ordinal)) reportPath = args[i];
                        else { stderr.WriteLine($"error: unknown option '{args[i]}'"); return ExitError; }
                        break;
                }
            }
            if (reportPath == null) { stderr.WriteLine("error: verify requires <signed-report.json>"); return ExitError; }
            if (keysPath == null) { stderr.WriteLine("error: verify requires --keys <keys.json>"); return ExitError; }
            if (segmentsDir == null) { stderr.WriteLine("error: verify requires --segments <dir>"); return ExitError; }
            if (checkpointsDir == null) { stderr.WriteLine("error: verify requires --checkpoints <dir>"); return ExitError; }

            var errors = new List<string>();
            var warnings = new List<string>();
            var facts = new Dictionary<string, object?>();
            if (offline) facts["offline"] = true;

            // 1) 解析签名报告 + canonical 重算
            SignedReport report = SignedReportCodec.Parse(File.ReadAllText(reportPath));
            string reportSha = report.ReportSha256();
            facts["report_sha256"] = reportSha;

            // 2) 报告验签（信封 canonical 字节 + 密钥状态机）
            KeyFile keys = KeyStore.Load(keysPath);
            ReportVerifier.Result signature = ReportVerifier.Verify(report, keys);
            errors.AddRange(signature.Errors);
            warnings.AddRange(signature.Warnings);

            // 3) 定位日志条目（信封 input.sha256 必须与条目一致）
            var log = PublicLogDirectory.Load(segmentsDir, checkpointsDir);
            warnings.AddRange(log.Diagnostics);
            LogEntry? entry;
            string? segmentFile;
            InclusionProof? proof = null;
            if (proofPath != null)
            {
                proof = ProofCodec.Parse(File.ReadAllText(proofPath));
                if (!log.TryFindEntryBySequence(proof.Seq, out entry, out segmentFile))
                {
                    errors.Add($"log has no entry with seq {proof.Seq}");
                    entry = null;
                }
            }
            else if (!log.TryFindEntryByReportSha(reportSha, out entry, out segmentFile))
            {
                errors.Add("report_sha256 not found in any segment (report never logged?)");
            }
            if (entry != null)
            {
                facts["seq"] = entry.Sequence;
                facts["logged_at"] = LogTimeText(entry.TimestampUtc);
                if (entry.ReportSha256 != reportSha)
                {
                    errors.Add("log entry report_sha256 mismatch (tampered report?)");
                }
                if (report.Envelope.TryGetValue("input", out object? inputObj)
                    && inputObj is Dictionary<string, object?> input
                    && input.TryGetValue("sha256", out object? inputShaObj) && inputShaObj is string inputSha
                    && inputSha != entry.InputSha256)
                {
                    errors.Add($"envelope input.sha256 '{inputSha}' does not match log entry '{entry.InputSha256}'");
                }
            }

            // 4) 树根对照 + 包含证明
            if (entry != null && log.Checkpoints.Count > 0)
            {
                Checkpoint? checkpoint;
                if (proof != null)
                {
                    checkpoint = PublicLogDirectory.FindCheckpointByTreeSize(log.Checkpoints, proof.TreeSize);
                    if (checkpoint == null)
                    {
                        errors.Add($"no checkpoint with tree_size {proof.TreeSize}");
                    }
                }
                else
                {
                    checkpoint = log.Checkpoints[log.Checkpoints.Count - 1];
                }

                if (checkpoint != null)
                {
                    facts["tree_size"] = checkpoint.TreeSize;
                    CheckpointVerification cpVerify = CheckpointVerifier.Verify(checkpoint, keys);
                    if (!cpVerify.Valid) errors.Add($"checkpoint #{checkpoint.TreeSize}: {cpVerify.Error}");
                    if (cpVerify.Warning != null) warnings.Add($"checkpoint #{checkpoint.TreeSize}: {cpVerify.Warning}");

                    if (checkpoint.TreeSize <= log.Entries.Count
                        && entry.Sequence - log.BaseSequence < checkpoint.TreeSize)
                    {
                        byte[] rebuilt;
                        try
                        {
                            rebuilt = log.RootAtSize(checkpoint.TreeSize);
                        }
                        catch (InvalidOperationException ex)
                        {
                            errors.Add(ex.Message);
                            rebuilt = Array.Empty<byte>();
                        }
                        byte[] expected = DecodeHashClaim(checkpoint.Sha256RootHash);
                        if (rebuilt.Length == 32 && !BytesEqual(rebuilt, expected))
                        {
                            errors.Add("rebuilt Merkle root does not match checkpoint (tampered segments?)");
                        }

                        if (proof != null)
                        {
                            if (proof.TreeSize != checkpoint.TreeSize)
                            {
                                errors.Add("proof tree_size does not match checkpoint");
                            }
                            if (!ProofChecker.Verify(entry, proof, log.BaseSequence, expected))
                            {
                                errors.Add("inclusion proof verification failed");
                            }
                        }
                        else if (rebuilt.Length == 32)
                        {
                            // 无 proof：自行重建路径验证（证明条目在 checkpoint 树内）
                            var leafHashes = new List<byte[]>((int)checkpoint.TreeSize);
                            for (int i = 0; i < checkpoint.TreeSize; i++)
                            {
                                leafHashes.Add(Rfc6962.LeafHash(Encoding.UTF8.GetBytes(log.Entries[i].ToCanonicalLine())));
                            }
                            byte[][] path = MerkleTree.FromLeafHashes(leafHashes)
                                .InclusionPath(entry.Sequence - log.BaseSequence);
                            if (!MerkleVerifier.VerifyInclusion(
                                    Rfc6962.LeafHash(Encoding.UTF8.GetBytes(entry.ToCanonicalLine())),
                                    entry.Sequence - log.BaseSequence, checkpoint.TreeSize, path, expected))
                            {
                                errors.Add("inclusion verification failed (entry not covered by checkpoint root)");
                            }
                        }
                    }
                    else if (entry.Sequence - log.BaseSequence >= checkpoint.TreeSize)
                    {
                        errors.Add($"entry seq {entry.Sequence} is not covered by any checkpoint (latest tree_size {checkpoint.TreeSize})");
                    }

                    // 5) checkpoint 链回溯 + 外部锚（截断链有锚见证时降级为警告，无锚则失败）
                    bool headTruncated = log.Checkpoints.Count > 0 && log.Checkpoints[0].PrevCheckpointHash != null;
                    foreach (string chainError in CheckpointChain.Validate(log.Checkpoints))
                    {
                        if (headTruncated && chainError.Contains("truncated chain", StringComparison.Ordinal))
                        {
                            warnings.Add(chainError);
                        }
                        else
                        {
                            errors.Add(chainError);
                        }
                    }
                    if (headTruncated)
                    {
                        AnchorWitness? witness = ResolveAnchor(anchor, log.Checkpoints, errors, warnings);
                        if (witness == null)
                        {
                            errors.Add("truncated checkpoint chain and no external anchor resolved — trust endpoint missing");
                        }
                        else
                        {
                            facts["anchored_on"] = witness.Date;
                        }
                    }
                    else if (anchor != null)
                    {
                        AnchorWitness? witness = ResolveAnchor(anchor, log.Checkpoints, errors, warnings);
                        if (witness != null) facts["anchored_on"] = witness.Date;
                    }
                }
            }
            else if (entry != null)
            {
                errors.Add("no checkpoints available — cannot verify tree root");
            }

            return Report(json, errors, warnings, facts, stdout, "verify");
        }

        // —— lookup：仅持同文件者（D5 第二用户路径）——

        private static int RunLookup(ReadOnlySpan<string> args, TextWriter stdout, TextWriter stderr)
        {
            string? inputSha = null, segmentsDir = null;
            bool json = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--input-sha256": inputSha = Next(args, ref i, "--input-sha256"); break;
                    case "--segments": segmentsDir = Next(args, ref i, "--segments"); break;
                    case "--json": json = true; break;
                    default:
                        stderr.WriteLine($"error: unknown option '{args[i]}'");
                        return ExitError;
                }
            }
            if (inputSha == null) { stderr.WriteLine("error: lookup requires --input-sha256 sha256:<64hex>"); return ExitError; }
            if (!IsValidShaClaim(inputSha)) { stderr.WriteLine("error: --input-sha256 must be 'sha256:' + 64 lowercase hex"); return ExitError; }
            if (segmentsDir == null) { stderr.WriteLine("error: lookup requires --segments <dir>"); return ExitError; }

            var log = PublicLogDirectory.Load(segmentsDir);
            List<(LogEntry Entry, string SegmentFile)> found = log.FindEntriesByInputSha(inputSha);
            if (found.Count == 0)
            {
                if (json)
                {
                    stdout.WriteLine(AigcTotal.Report.CanonicalJson.Serialize(new Dictionary<string, object?>
                    {
                        ["found"] = 0, ["input_sha256"] = inputSha,
                    }));
                }
                else
                {
                    stdout.WriteLine($"no log entry for input {inputSha}");
                }
                return ExitInvalid;
            }

            if (json)
            {
                var hits = new List<object?>();
                foreach (var (entry, file) in found)
                {
                    hits.Add(new Dictionary<string, object?>
                    {
                        ["seq"] = entry.Sequence,
                        ["timestamp"] = LogTimeText(entry.TimestampUtc),
                        ["report_sha256"] = entry.ReportSha256,
                        ["segment"] = file,
                    });
                }
                stdout.WriteLine(AigcTotal.Report.CanonicalJson.Serialize(new Dictionary<string, object?>
                {
                    ["found"] = (long)found.Count,
                    ["input_sha256"] = inputSha,
                    ["entries"] = hits,
                }));
            }
            else
            {
                foreach (var (entry, file) in found)
                {
                    stdout.WriteLine($"seq={entry.Sequence} logged_at={LogTimeText(entry.TimestampUtc)} " +
                        $"report={entry.ReportSha256} segment={file}");
                }
            }
            return ExitValid;
        }

        // —— audit：全量重建审计 ——

        private static int RunAudit(ReadOnlySpan<string> args, TextWriter stdout, TextWriter stderr)
        {
            string? segmentsDir = null, checkpointsDir = null, keysPath = null, anchor = null;
            bool json = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--segments": segmentsDir = Next(args, ref i, "--segments"); break;
                    case "--checkpoints": checkpointsDir = Next(args, ref i, "--checkpoints"); break;
                    case "--keys": keysPath = Next(args, ref i, "--keys"); break;
                    case "--anchor": anchor = Next(args, ref i, "--anchor"); break;
                    case "--json": json = true; break;
                    default:
                        stderr.WriteLine($"error: unknown option '{args[i]}'");
                        return ExitError;
                }
            }
            if (segmentsDir == null) { stderr.WriteLine("error: audit requires --segments <dir>"); return ExitError; }
            if (checkpointsDir == null) { stderr.WriteLine("error: audit requires --checkpoints <dir>"); return ExitError; }
            if (keysPath == null) { stderr.WriteLine("error: audit requires --keys <keys.json>"); return ExitError; }

            var errors = new List<string>();
            var warnings = new List<string>();
            var facts = new Dictionary<string, object?>();

            var log = PublicLogDirectory.Load(segmentsDir, checkpointsDir);
            facts["entries"] = (long)log.Entries.Count;
            facts["segments"] = (long)log.SegmentFiles.Count;
            facts["checkpoints"] = (long)log.Checkpoints.Count;
            warnings.AddRange(log.Diagnostics);
            if (log.Entries.Count == 0)
            {
                errors.Add("log has no entries");
            }

            // 链校验（含链首截断检测——audit 语义：无外部锚时截断链即失败）
            errors.AddRange(CheckpointChain.Validate(log.Checkpoints));

            KeyFile keys = KeyStore.Load(keysPath);
            long maxCovered = 0;
            foreach (Checkpoint checkpoint in log.Checkpoints)
            {
                CheckpointVerification verify = CheckpointVerifier.Verify(checkpoint, keys);
                if (!verify.Valid)
                {
                    errors.Add($"checkpoint #{checkpoint.TreeSize}: {verify.Error}");
                    continue;
                }
                if (verify.Warning != null)
                {
                    warnings.Add($"checkpoint #{checkpoint.TreeSize}: {verify.Warning}");
                }
                byte[] rebuilt;
                try
                {
                    rebuilt = log.RootAtSize(checkpoint.TreeSize);
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add($"checkpoint #{checkpoint.TreeSize}: {ex.Message}");
                    continue;
                }
                if (!BytesEqual(rebuilt, DecodeHashClaim(checkpoint.Sha256RootHash)))
                {
                    errors.Add($"checkpoint #{checkpoint.TreeSize}: rebuilt root mismatch (tampered segments?)");
                }
                maxCovered = checkpoint.TreeSize;
            }
            if (log.Entries.Count > 0 && maxCovered < log.Entries.Count)
            {
                warnings.Add($"{log.Entries.Count - maxCovered} trailing entries not yet covered by any checkpoint");
            }

            if (anchor != null)
            {
                AnchorWitness? witness = ResolveAnchor(anchor, log.Checkpoints, errors, warnings);
                if (witness != null)
                {
                    facts["anchored_on"] = witness.Date;
                }
                else if (log.Checkpoints.Count > 0)
                {
                    errors.Add("anchor archive contains none of the local checkpoints");
                }
            }
            return Report(json, errors, warnings, facts, stdout, "audit");
        }

        // —— 公共 ——

        private static AnchorWitness? ResolveAnchor(string? anchor, IReadOnlyList<Checkpoint> checkpoints,
            List<string> errors, List<string> warnings)
        {
            if (anchor == null) return null;
            try
            {
                // anchor 可以是单个 tarball 或归档目录
                var provider = new LocalAnchorProvider(anchor);
                foreach (Checkpoint checkpoint in checkpoints)
                {
                    AnchorWitness? witness = provider.Query(CheckpointBuilder.FingerprintOf(checkpoint));
                    if (witness != null) return witness;
                }
                // 截断链：链首 prev 指向的 checkpoint 不在本地——用其指纹向锚归档查见证
                string? prevHash = checkpoints.Count > 0 ? checkpoints[0].PrevCheckpointHash : null;
                if (prevHash != null)
                {
                    return provider.QueryChainEndpoint(prevHash);
                }
                return null;
            }
            catch (FormatException ex)
            {
                errors.Add("anchor archive failed integrity check: " + ex.Message);
                return null;
            }
            catch (IOException ex)
            {
                errors.Add("cannot read anchor archive: " + ex.Message);
                return null;
            }
        }

        private static int Report(bool json, List<string> errors, List<string> warnings,
            Dictionary<string, object?> facts, TextWriter stdout, string command)
        {
            bool valid = errors.Count == 0;
            facts["valid"] = valid;
            facts["errors"] = errors;
            facts["warnings"] = warnings;

            if (json)
            {
                var doc = new Dictionary<string, object?> { [command] = facts };
                stdout.WriteLine(AigcTotal.Report.CanonicalJson.Serialize(doc));
            }
            else
            {
                foreach (var pair in facts)
                {
                    if (pair.Key == "errors" || pair.Key == "warnings" || pair.Key == "valid") continue;
                    if (pair.Value is List<string> list && list.Count == 0) continue;
                    stdout.WriteLine($"{pair.Key}: {pair.Value}");
                }
                foreach (string warning in warnings)
                {
                    stdout.WriteLine($"warning: {warning}");
                }
                foreach (string error in errors)
                {
                    stdout.WriteLine($"FAIL: {error}");
                }
                stdout.WriteLine(valid ? "RESULT: VALID" : "RESULT: INVALID");
            }
            return valid ? ExitValid : ExitInvalid;
        }

        private static string Next(ReadOnlySpan<string> args, ref int i, string option)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"{option} requires a value");
            return args[++i];
        }

        private static bool IsValidShaClaim(string value)
        {
            if (value == null || value.Length != 7 + 64 || !value.StartsWith("sha256:", StringComparison.Ordinal)) return false;
            for (int i = 7; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        private static byte[] DecodeHashClaim(string claim)
        {
            byte[] bytes = new byte[32];
            for (int i = 0; i < 32; i++)
            {
                bytes[i] = Convert.ToByte(claim.Substring(7 + i * 2, 2), 16);
            }
            return bytes;
        }

        private static string LogTimeText(DateTimeOffset value) =>
            value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static string ToolVersion =>
            typeof(VerifyCli).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        private static void WriteUsage(TextWriter writer)
        {
            writer.WriteLine("aigc-verify — third-party verification of the AigcTotal transparency log");
            writer.WriteLine();
            writer.WriteLine("usage:");
            writer.WriteLine("  aigc-verify verify <signed-report.json> --keys <keys.json> --segments <dir>");
            writer.WriteLine("                 --checkpoints <dir> [--proof proof.json] [--anchor <tarball|dir>]");
            writer.WriteLine("                 [--offline] [--json]");
            writer.WriteLine("  aigc-verify lookup --input-sha256 sha256:<64hex> --segments <dir> [--json]");
            writer.WriteLine("  aigc-verify audit --segments <dir> --checkpoints <dir> --keys <keys.json>");
            writer.WriteLine("                 [--anchor <tarball|dir>] [--json]");
            writer.WriteLine();
            writer.WriteLine("verify: five-step chain — envelope canonical re-hash, ES256 report signature,");
            writer.WriteLine("        Merkle root rebuild vs checkpoint, inclusion proof, checkpoint chain to");
            writer.WriteLine("        an external anchor (truncated chains REQUIRE an anchor).");
            writer.WriteLine("lookup: prove a file (by its sha256) was logged, and when; entry details printed.");
            writer.WriteLine("audit:  full rebuild — chain-head truncation detection, per-checkpoint signature");
            writer.WriteLine("        and prefix-root comparison, optional anchor archive integrity check.");
            writer.WriteLine();
            writer.WriteLine("exit codes: 0 valid  1 verification failed  10 error");
            writer.WriteLine();
            writer.WriteLine("the log artifacts are all local files; no network access is ever required.");
        }
    }
}
