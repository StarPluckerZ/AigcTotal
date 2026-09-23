using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AigcTotal.Log.Segments
{
    public sealed class SegmentWriterOptions
    {
        public string Directory { get; set; } = "";

        /// <summary>段内条数上限（默认 8192，先到者滚动）。</summary>
        public long MaxEntries { get; set; } = 8192;

        /// <summary>段时间跨度上限（默认 1 小时，先到者滚动）；按条目自身时间戳计算，不取墙上时钟。</summary>
        public TimeSpan MaxDuration { get; set; } = TimeSpan.FromHours(1);
    }

    /// <summary>
    /// append-only 段写侧（单写者契约，非线程安全——与 server 的单写者原则一致）。
    /// 段文件名 = 首条序号（D12 零填充）.jsonl；行 = 条目 canonical JSON + LF；
    /// 每条 Flush（无 fsync——崩溃尾部残行由读侧容忍并诊断）。滚动阈值按条目自身时间戳计算。
    /// </summary>
    public sealed class SegmentWriter : IDisposable
    {
        private readonly SegmentWriterOptions _options;
        private StreamWriter? _writer;

        public SegmentWriter(SegmentWriterOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.Directory)) throw new ArgumentException("directory required", nameof(options));
            if (options.MaxEntries <= 0) throw new ArgumentException("MaxEntries must be positive", nameof(options));
            _options = options;
            System.IO.Directory.CreateDirectory(options.Directory);
        }

        public long LastSequence { get; private set; } = -1;

        public long CurrentEntryCount { get; private set; }

        public string? CurrentSegmentPath { get; private set; }

        public void Append(LogEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (entry.Sequence < 0) throw new ArgumentException("sequence must be non-negative", nameof(entry));
            if (entry.Sequence <= LastSequence)
            {
                throw new ArgumentException($"sequence must be strictly increasing (last={LastSequence})", nameof(entry));
            }
            if (!TokenFormat.IsValidSha256Claim(entry.ReportSha256))
            {
                throw new ArgumentException("report_sha256 must be 'sha256:' + 64 lowercase hex", nameof(entry));
            }

            if (_writer == null)
            {
                StartSegment(entry);
            }
            else if (CurrentEntryCount >= _options.MaxEntries
                || entry.TimestampUtc - FirstEntryTimestamp >= _options.MaxDuration)
            {
                CloseCurrent();
                StartSegment(entry);
            }

            _writer!.Write(entry.ToCanonicalLine());
            _writer.Write('\n');
            _writer.Flush();
            LastSequence = entry.Sequence;
            CurrentEntryCount++;
        }

        private DateTimeOffset FirstEntryTimestamp { get; set; }

        private void StartSegment(LogEntry firstEntry)
        {
            CurrentSegmentPath = Path.Combine(
                _options.Directory, firstEntry.Sequence.ToString("D12", CultureInfo.InvariantCulture) + ".jsonl");
            _writer = new StreamWriter(
                new FileStream(CurrentSegmentPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            FirstEntryTimestamp = firstEntry.TimestampUtc;
            CurrentEntryCount = 0;
        }

        private void CloseCurrent()
        {
            _writer?.Dispose();
            _writer = null;
        }

        public void Dispose() => CloseCurrent();
    }
}
