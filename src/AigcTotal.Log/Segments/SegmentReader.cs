using System;
using System.Collections.Generic;
using System.IO;
using AigcTotal.Report;

namespace AigcTotal.Log.Segments
{
    public sealed class SegmentReadResult
    {
        internal SegmentReadResult(IReadOnlyList<LogEntry> entries, IReadOnlyList<string> diagnostics)
        {
            Entries = entries;
            Diagnostics = diagnostics;
        }

        public IReadOnlyList<LogEntry> Entries { get; }

        /// <summary>读侧诊断（崩溃尾部残行、畸形行等）；段的真实性由 checkpoint 树根对照负责，此处只收集。</summary>
        public IReadOnlyList<string> Diagnostics { get; }
    }

    /// <summary>段读侧：容忍崩溃尾部残行与畸形行（跳过 + 诊断，绝不抛出）；真实性由重建树根对照保证。</summary>
    public static class SegmentReader
    {
        public static SegmentReadResult Read(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("segment not found", path);

            var entries = new List<LogEntry>();
            var diagnostics = new List<string>();
            string raw;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new System.IO.StreamReader(stream))
            {
                raw = reader.ReadToEnd();
            }

            int lineStart = 0;
            int lineNumber = 0;
            while (lineStart < raw.Length)
            {
                int eol = raw.IndexOf('\n', lineStart);
                bool terminated = eol >= 0;
                string line = terminated
                    ? raw.Substring(lineStart, eol - lineStart)
                    : raw.Substring(lineStart);
                lineNumber++;

                if (line.Length > 0 && line[line.Length - 1] == '\r') line = line.Substring(0, line.Length - 1);

                if (!terminated)
                {
                    // 崩溃尾部残行（无 LF 结尾）：忽略并诊断
                    diagnostics.Add($"line {lineNumber}: trailing partial line ignored");
                    break;
                }
                if (line.Length > 0)
                {
                    if (TryParseLine(line, out LogEntry? entry, out string? error))
                    {
                        entries.Add(entry!);
                    }
                    else
                    {
                        diagnostics.Add($"line {lineNumber}: {error}");
                    }
                }
                lineStart = eol + 1;
            }

            return new SegmentReadResult(entries, diagnostics);
        }

        private static bool TryParseLine(string line, out LogEntry? entry, out string? error)
        {
            entry = null;
            error = null;
            Dictionary<string, object?> doc;
            try
            {
                doc = CanonicalJson.Deserialize(line);
            }
            catch (FormatException ex)
            {
                error = "malformed json (" + ex.Message + ")";
                return false;
            }
            if (doc.Count != 3
                || !doc.TryGetValue("seq", out object? seqObj)
                || !doc.TryGetValue("timestamp", out object? tsObj)
                || !doc.TryGetValue("report_sha256", out object? hashObj))
            {
                error = "unexpected field set";
                return false;
            }
            if (seqObj is not long seq || tsObj is not string ts || hashObj is not string hash)
            {
                error = "field type mismatch";
                return false;
            }
            if (!LogTime.TryParse(ts, out DateTimeOffset timestamp))
            {
                error = "bad timestamp";
                return false;
            }
            if (!TokenFormat.IsValidSha256Claim(hash))
            {
                error = "bad report_sha256 format";
                return false;
            }
            entry = new LogEntry(seq, timestamp, hash);
            return true;
        }
    }
}
