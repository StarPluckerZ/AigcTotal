using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AigcTotal.GB45438;
using AigcTotal.GB45438.Verdict;
using AigcTotal.Report;

namespace AigcTotal.Report.Cli
{
    /// <summary>
    /// aigc-report：校验报告官方参考实现。文件 → 核查 → schema v1 canonical JSON 信封。
    /// 退出码（多文件取最严重）：0=合规 1=不合规 2=未检出 3=无法判定 10=处理错误。
    /// </summary>
    public static class ReportCli
    {
        private const int ExitCompliant = 0;
        private const int ExitNoncompliant = 1;
        private const int ExitNotFound = 2;
        private const int ExitInconclusive = 3;
        private const int ExitError = 10;

        public static int Main(string[] args)
        {
            return Run(args, Console.Out, Console.Error);
        }

        public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
        {
            ArgumentNullException.ThrowIfNull(args);

            var files = new List<string>();
            bool toStdout = false, pretty = false;
            string? outDir = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "--help" || arg == "-h")
                {
                    WriteUsage(stdout);
                    return ExitCompliant;
                }
                if (arg == "--version")
                {
                    stdout.WriteLine(ToolVersion);
                    return ExitCompliant;
                }
                if (arg == "--stdout") { toStdout = true; continue; }
                if (arg == "--pretty") { pretty = true; continue; }
                if (arg == "--out")
                {
                    if (i + 1 >= args.Length)
                    {
                        stderr.WriteLine("error: --out requires a directory argument");
                        return ExitError;
                    }
                    outDir = args[++i];
                    continue;
                }
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    stderr.WriteLine($"error: unknown option {arg}");
                    WriteUsage(stderr);
                    return ExitError;
                }
                files.Add(arg);
            }

            if (files.Count == 0)
            {
                WriteUsage(stderr);
                return ExitError;
            }
            if (toStdout && files.Count > 1)
            {
                stderr.WriteLine("error: --stdout supports a single input file only");
                return ExitError;
            }

            string toolVersion = ToolVersion;
            int worst = ExitCompliant;

            foreach (string file in files)
            {
                int code = ProcessFile(file, toStdout, pretty, outDir, toolVersion, stdout, stderr);
                if (code > worst || worst == ExitCompliant)
                {
                    // 严重度排序：10 > 1 > 3 > 2 > 0
                    worst = Severity(code) > Severity(worst) ? code : worst;
                }
            }
            return worst;
        }

        private static int ProcessFile(string file, bool toStdout, bool pretty, string? outDir,
            string toolVersion, TextWriter stdout, TextWriter stderr)
        {
            if (!File.Exists(file))
            {
                stderr.WriteLine($"error: file not found: {file}");
                return ExitError;
            }

            try
            {
                string inputHash;
                long inputLength;
                VerificationResult result;
                using (var stream = File.OpenRead(file))
                {
                    inputLength = stream.Length;
                    inputHash = "sha256:" + HashStream(stream);
                    result = AigcLabelVerifier.Verify(stream);
                }

                ReportEnvelope envelope = AigcReportBuilder.Build(
                    result, inputHash, inputLength, "aigc-report", toolVersion);

                string json = pretty ? CanonicalJson.SerializePretty(envelope.Document) : envelope.CanonicalJson;
                string? destination = null;
                if (toStdout)
                {
                    stdout.Write(json);
                    stdout.WriteLine();
                }
                else
                {
                    string dir = outDir ?? Path.GetDirectoryName(Path.GetFullPath(file))!;
                    Directory.CreateDirectory(dir);
                    destination = Path.Combine(dir, Path.GetFileName(file) + ".report.json");
                    File.WriteAllText(destination, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }

                stderr.WriteLine($"{file}: {VerdictTokens.ToToken(result.Verdict)} | report={envelope.ReportSha256}"
                    + (destination != null ? $" | {destination}" : string.Empty));
                return ExitCodeOf(result.Verdict);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                stderr.WriteLine($"error: cannot read {file}: {ex.Message}");
                return ExitError;
            }
        }

        private static int ExitCodeOf(VerdictKind verdict)
        {
            switch (verdict)
            {
                case VerdictKind.Compliant: return ExitCompliant;
                case VerdictKind.Noncompliant: return ExitNoncompliant;
                case VerdictKind.NotFound: return ExitNotFound;
                case VerdictKind.Inconclusive: return ExitInconclusive;
                default: return ExitError;
            }
        }

        private static int Severity(int code)
        {
            switch (code)
            {
                case ExitError: return 4;
                case ExitNoncompliant: return 3;
                case ExitInconclusive: return 2;
                case ExitNotFound: return 1;
                default: return 0;
            }
        }

        private static string ToolVersion =>
            typeof(ReportCli).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        private static string HashStream(Stream stream)
        {
            stream.Seek(0, SeekOrigin.Begin);
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                var sb = new StringBuilder(64);
                foreach (byte b in sha.Hash!)
                {
                    sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        private static void WriteUsage(TextWriter writer)
        {
            writer.WriteLine("aigc-report — AIGC implicit label verification report (GB 45438-2025)");
            writer.WriteLine();
            writer.WriteLine("usage: aigc-report [options] <file>...");
            writer.WriteLine();
            writer.WriteLine("options:");
            writer.WriteLine("  --stdout        write canonical JSON of a single file to stdout");
            writer.WriteLine("  --out <dir>     directory for <file>.report.json outputs (default: beside input)");
            writer.WriteLine("  --pretty        indented, human-readable (NOT canonical; do not hash/verify)");
            writer.WriteLine("  --version       print tool version");
            writer.WriteLine();
            writer.WriteLine("exit codes (worst of all inputs):");
            writer.WriteLine("  0 compliant  1 noncompliant  2 not_found  3 inconclusive  10 error");
            writer.WriteLine();
            writer.WriteLine("default output is canonical JSON (RFC 8785 subset): the exact bytes that are");
            writer.WriteLine("hashed, signed and logged by AigcTotal attestation reports.");
        }
    }
}
