using System;
using System.Collections.Generic;
using System.IO;
using AigcTotal.Log.Checkpoints;
using AigcTotal.Log.Merkle;
using AigcTotal.Log.Segments;

namespace AigcTotal.Log
{
    /// <summary>
    /// 公开物目录编目（信任路径，零数据库）：segments/ 按文件名（= 首条序号 D12）有序枚举 →
    /// 全量条目；checkpoints/ 解析后按 tree_size 升序。树根对照、条目定位、链校验均由此展开——
    /// 公开物 + 本类 + aigc-verify 即完整的第三方独立验证材料（D4-1）。
    /// </summary>
    public sealed class PublicLogDirectory
    {
        private PublicLogDirectory(string segmentsDir, string? checkpointsDir,
            IReadOnlyList<string> segmentFiles, IReadOnlyList<LogEntry> entries,
            IReadOnlyList<string> diagnostics, IReadOnlyList<Checkpoint> checkpoints)
        {
            SegmentsDirectory = segmentsDir;
            CheckpointsDirectory = checkpointsDir;
            SegmentFiles = segmentFiles;
            Entries = entries;
            Diagnostics = diagnostics;
            Checkpoints = checkpoints;
        }

        public string SegmentsDirectory { get; }
        public string? CheckpointsDirectory { get; }

        /// <summary>段文件（按首条序号升序；D12 定长名保证字典序 = 数值序）。</summary>
        public IReadOnlyList<string> SegmentFiles { get; }

        /// <summary>全日志条目（跨段按 seq 升序拼接）。</summary>
        public IReadOnlyList<LogEntry> Entries { get; }

        /// <summary>段读侧诊断（崩溃残行/畸形行/序号断裂）。</summary>
        public IReadOnlyList<string> Diagnostics { get; }

        /// <summary>checkpoint（按 tree_size 升序）。</summary>
        public IReadOnlyList<Checkpoint> Checkpoints { get; }

        /// <summary>全日志首条序号（空日志无定义）。</summary>
        public long BaseSequence => Entries.Count > 0 ? Entries[0].Sequence : throw new InvalidOperationException("log is empty");

        public static PublicLogDirectory Load(string segmentsDir, string? checkpointsDir = null)
        {
            if (segmentsDir == null) throw new ArgumentNullException(nameof(segmentsDir));
            if (!Directory.Exists(segmentsDir)) throw new DirectoryNotFoundException("segments dir not found: " + segmentsDir);

            var segmentFiles = new List<string>(Directory.GetFiles(segmentsDir, "*.jsonl"));
            segmentFiles.Sort(StringComparer.Ordinal);

            var entries = new List<LogEntry>();
            var diagnostics = new List<string>();
            long lastSeq = -1;
            string? lastFile = null;
            foreach (string file in segmentFiles)
            {
                SegmentReadResult result = SegmentReader.Read(file);
                foreach (LogEntry entry in result.Entries)
                {
                    if (entry.Sequence <= lastSeq)
                    {
                        diagnostics.Add($"{Path.GetFileName(file)}: seq {entry.Sequence} not increasing after {lastSeq}"
                            + (lastFile != null ? $" (in {lastFile})" : ""));
                    }
                    lastSeq = entry.Sequence;
                    lastFile = Path.GetFileName(file);
                    entries.Add(entry);
                }
                foreach (string diag in result.Diagnostics)
                {
                    diagnostics.Add($"{Path.GetFileName(file)}: {diag}");
                }
            }

            var checkpoints = new List<Checkpoint>();
            if (checkpointsDir != null)
            {
                if (!Directory.Exists(checkpointsDir))
                {
                    throw new DirectoryNotFoundException("checkpoints dir not found: " + checkpointsDir);
                }
                var files = new List<string>(Directory.GetFiles(checkpointsDir, "*.json"));
                files.Sort(StringComparer.Ordinal);
                foreach (string file in files)
                {
                    checkpoints.Add(CheckpointCodec.Parse(File.ReadAllText(file)));
                }
                checkpoints.Sort((a, b) => a.TreeSize.CompareTo(b.TreeSize));
            }
            return new PublicLogDirectory(segmentsDir, checkpointsDir, segmentFiles, entries, diagnostics, checkpoints);
        }

        /// <summary>由段重建前 treeSize 条的树根（treeSize 超出条目数 → InvalidOperationException）。</summary>
        public byte[] RootAtSize(long treeSize)
        {
            if (treeSize < 0 || treeSize > Entries.Count)
            {
                throw new InvalidOperationException($"tree_size {treeSize} not covered by {Entries.Count} log entries");
            }
            var prefix = new List<LogEntry>((int)treeSize);
            for (int i = 0; i < treeSize; i++) prefix.Add(Entries[i]);
            return CheckpointBuilder.RootHashFromEntries(prefix);
        }

        /// <summary>按 report_sha256 定位条目（持报告者路径；无 proof 时的兜底扫描）。</summary>
        public bool TryFindEntryByReportSha(string reportSha256, out LogEntry? entry, out string? segmentFile)
        {
            foreach (string file in SegmentFiles)
            {
                foreach (LogEntry candidate in SegmentReader.Read(file).Entries)
                {
                    if (candidate.ReportSha256 == reportSha256)
                    {
                        entry = candidate;
                        segmentFile = Path.GetFileName(file);
                        return true;
                    }
                }
            }
            entry = null;
            segmentFile = null;
            return false;
        }

        /// <summary>按 seq 定位条目（有 proof 时：段文件名 D12 有序 → 二分定位所属段）。</summary>
        public bool TryFindEntryBySequence(long seq, out LogEntry? entry, out string? segmentFile)
        {
            int lo = 0, hi = SegmentFiles.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                long segmentBase = ParseSegmentName(SegmentFiles[mid]);
                if (seq < segmentBase) hi = mid - 1;
                else if (mid + 1 < SegmentFiles.Count && seq >= ParseSegmentName(SegmentFiles[mid + 1])) lo = mid + 1;
                else
                {
                    foreach (LogEntry candidate in SegmentReader.Read(SegmentFiles[mid]).Entries)
                    {
                        if (candidate.Sequence == seq)
                        {
                            entry = candidate;
                            segmentFile = Path.GetFileName(SegmentFiles[mid]);
                            return true;
                        }
                    }
                    break;
                }
            }
            entry = null;
            segmentFile = null;
            return false;
        }

        /// <summary>按 input_sha256 定位条目（D5 第二用户路径：仅持同文件者证明"曾被核查 + 时间"）。</summary>
        public List<(LogEntry Entry, string SegmentFile)> FindEntriesByInputSha(string inputSha256)
        {
            var found = new List<(LogEntry, string)>();
            foreach (string file in SegmentFiles)
            {
                foreach (LogEntry candidate in SegmentReader.Read(file).Entries)
                {
                    if (candidate.InputSha256 == inputSha256)
                    {
                        found.Add((candidate, Path.GetFileName(file)));
                    }
                }
            }
            return found;
        }

        public static Checkpoint? FindCheckpointByTreeSize(IReadOnlyList<Checkpoint> checkpoints, long treeSize)
        {
            foreach (Checkpoint cp in checkpoints)
            {
                if (cp.TreeSize == treeSize) return cp;
            }
            return null;
        }

        private static long ParseSegmentName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            return long.TryParse(name,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long value)
                ? value
                : throw new FormatException($"segment file name is not a sequence number: {name}");
        }
    }
}
